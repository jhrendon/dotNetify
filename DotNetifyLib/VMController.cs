﻿/* 
Copyright 2016 Dicky Suryadi

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Windows.Input;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using DotNetify.Security;

namespace DotNetify
{
   /// <summary>
   /// This class manages instantiations and updates of view models as requested by browser clients.
   /// </summary>
   public class VMController : IDisposable
   {
      /// <summary>
      /// Exception that gets thrown when the VMController cannot resolve a JSON view model update from the client.
      /// </summary>
      public class UnresolvedVMUpdateException : Exception { }

      #region Delegates

      /// <summary>
      /// Delegate used by the view model to provide response back to the client.
      /// </summary>
      /// <param name="connectionId">Identifies the connection.</param>
      /// <param name="vmId">Identifies the view model providing the response.</param>
      /// <param name="vmData">Response data.</param>
      public delegate void VMResponseDelegate(string connectionId, string vmId, string vmData);

      /// <summary>
      /// Delegate to create view models.
      /// </summary>
      /// <param name="type">Class type.</param>
      /// <param name="args">Optional constructor arguments.</param>
      /// <returns>Object of type T.</returns>
      public delegate object CreateInstanceDelegate(Type type, object[] args);

      #endregion

      #region Fields

      /// <summary>
      /// This class encapsulates a view model information.
      /// </summary>
      protected class VMInfo
      {
         /// <summary>
         /// Instance of a view model.
         /// </summary>
         public BaseVM Instance { get; set; }

         /// <summary>
         /// Identifies the SignalR connection to the browser client associated with the view model.
         /// </summary>
         public string ConnectionId { get; set; }
      }

      /// <summary>
      /// List of known view model classes.
      /// </summary>
      protected static List<Type> _vmTypes = new List<Type>();

      /// <summary>
      /// List of registered assemblies.
      /// </summary>
      protected static List<string> _registeredAssemblies = new List<string>();

      /// <summary>
      /// Active instances of view models.
      /// </summary>
      protected ConcurrentDictionary<string, VMInfo> _activeVMs = new ConcurrentDictionary<string, VMInfo>();

      /// <summary>
      /// Function invoked by the view model to provide response back to the client.
      /// </summary>
      protected readonly VMResponseDelegate _vmResponse;

      #endregion

      /// <summary>
      /// Delegate to override default mechanism used for creating view model instances.
      /// </summary>
      public static CreateInstanceDelegate CreateInstance { get; set; } = (type, args) => Activator.CreateInstance(type, args);

      /// <summary>
      /// Security context.
      /// </summary>
      internal IPrincipal Principal;

      /// <summary>
      /// Constructor.
      /// </summary>
      /// <param name="vmResponse">Function invoked by the view model to provide response back to the client.</param>
      public VMController(VMResponseDelegate vmResponse)
      {
         _vmResponse = vmResponse;
         if (_vmResponse == null)
            throw new ArgumentNullException();
      }

      /// <summary>
      /// Disposes active view models.
      /// </summary>
      public virtual void Dispose()
      {
         foreach (var kvp in _activeVMs)
         {
            kvp.Value.Instance.RequestPushUpdates -= VmInstance_RequestPushUpdates;
            kvp.Value.Instance.Dispose();
         }
      }

      /// <summary>
      /// Registers all view model types in an assembly.
      /// </summary>
      /// <param name="vmAssembly">Assembly.</param>
      public static void RegisterAssembly(Assembly vmAssembly)
      {
         if (vmAssembly == null)
            throw new ArgumentNullException();

         if (_registeredAssemblies.Exists(i => i == vmAssembly.FullName))
            return;

         _registeredAssemblies.Add(vmAssembly.FullName);

         bool hasVMTypes = false;
         foreach (Type vmType in vmAssembly.GetExportedTypes().Where(i => typeof(BaseVM).IsAssignableFrom(i)))
         {
            hasVMTypes = true;
            if (_vmTypes.Find(i => i == vmType) == null)
               _vmTypes.Add(vmType);
            else
               throw new Exception($"ERROR: View model '{vmType.Name}' was already registered by another assembly!");
         }

         if (!hasVMTypes)
            throw new Exception($"ERROR: Assembly '{vmAssembly.GetName().Name}' does not define any view model!");
      }

      /// <summary>
      /// Handles a request for a view model from a browser client.
      /// </summary>
      /// <param name="connectionId">Identifies the client connection.</param>
      /// <param name="vmId">Identifies the view model.</param>
      /// <param name="vmArg">Optional view model's initialization argument.</param>
      public virtual void OnRequestVM(string connectionId, string vmId, object vmArg = null)
      {
         // Create a new view model instance whose class name is matching the given VMId.
         BaseVM vmInstance = !_activeVMs.ContainsKey(vmId) ? CreateVM(vmId, vmArg) : _activeVMs[vmId].Instance;

         // Make sure current security context is authorized to access the view model.
         if (!IsAuthorized(vmInstance))
            throw new UnauthorizedAccessException();

         var vmData = Serialize(vmInstance);

         // Send the view model data back to the browser client.
         _vmResponse(connectionId, vmId, vmData);

         // Reset the changed property states.
         vmInstance.AcceptChangedProperties();

         // Add the view model instance to the controller.
         if (!_activeVMs.ContainsKey(vmId))
         {
            _activeVMs.TryAdd(vmId, new VMInfo { Instance = vmInstance, ConnectionId = connectionId });
            vmInstance.RequestPushUpdates += VmInstance_RequestPushUpdates;
         }
         else
            _activeVMs[vmId].ConnectionId = connectionId;

         // If this request causes other view models to change, push those new values back to the client.
         PushUpdates();
      }

      /// <summary>
      /// Handles view model update from a browser client.
      /// </summary>
      /// <param name="connectionId">Identifies the client connection.</param>
      /// <param name="vmId">Identifies the view model.</param>
      /// <param name="iData">View model update.</param>
      public virtual void OnUpdateVM(string connectionId, string vmId, Dictionary<string, object> data)
      {
         bool isRecreated = false;
         if (!_activeVMs.ContainsKey(vmId))
         {
            // No view model found; it must have expired and needs to be recreated.
            isRecreated = true;
            OnRequestVM(connectionId, vmId);
            if (!_activeVMs.ContainsKey(vmId))
               return;
         }

         // Update the new values from the client to the server view model.
         var vmInstance = _activeVMs[vmId].Instance;

         // Make sure current security context is authorized to access the view model.
         if (!IsAuthorized(vmInstance))
            throw new UnauthorizedAccessException();

         lock (vmInstance)
         {
            foreach (var kvp in data)
            {
               UpdateVM(vmInstance, kvp.Key, kvp.Value != null ? kvp.Value.ToString() : "");

               // If the view model was recreated, include the changes that trigger this update to overwrite their initial values.
               if (isRecreated && !vmInstance.ChangedProperties.ContainsKey(kvp.Key))
                  vmInstance.ChangedProperties.TryAdd(kvp.Key, kvp.Value);
            }
         }

         // If the updates cause some properties of this and other view models to change, push those new values back to the client.
         PushUpdates();
      }

      /// <summary>
      /// Handles request from a browser client to remove a view model.
      /// </summary>
      /// <param name="connectionId">Identifies the client connection.</param>
      /// <param name="vmId">Identifies the view model.</param>
      public virtual void OnDisposeVM(string connectionId, string vmId)
      {
         if (_activeVMs.ContainsKey(vmId))
         {
            VMInfo vmInfo;
            if (_activeVMs.TryRemove(vmId, out vmInfo))
            {
               vmInfo.Instance.RequestPushUpdates -= VmInstance_RequestPushUpdates;
               vmInfo.Instance.Dispose();
            }
         }
      }

      /// <summary>
      /// Creates a view model.
      /// </summary>
      /// <param name="vmId">Identifies the view model.</param>
      /// <param name="vmArg">Optional view model's initialization argument.</param>
      /// <returns>View model instance.</returns>
      protected virtual BaseVM CreateVM(string vmId, object vmArg = null)
      {
         BaseVM vmInstance = null;

         // If the view model Id is in the form of a delimited path, it has a master view model.
         BaseVM masterVM = null;
         var path = vmId.Split('.');
         if (path.Length > 1)
         {
            // Get the master view model; create the instance if it doesn't exist.
            var masterVMId = vmId.Remove(vmId.LastIndexOf('.'));
            lock (_activeVMs)
            {
               if (!_activeVMs.ContainsKey(masterVMId))
               {
                  masterVM = CreateVM(masterVMId);
                  _activeVMs.TryAdd(masterVMId, new VMInfo { Instance = masterVM });
               }
               else
                  masterVM = _activeVMs[masterVMId].Instance;
            }
            vmId = vmId.Remove(0, vmId.LastIndexOf('.') + 1);
         }

         // If the view model Id contains instance Id, parse it out.
         string vmTypeName = vmId;
         string vmInstanceId = null;
         if (vmTypeName.Contains('$'))
         {
            path = vmTypeName.Split('$');
            vmTypeName = path[0];
            vmInstanceId = path[1];
         }

         // Get the view model instance from the master view model.
         if (masterVM != null)
            vmInstance = masterVM.GetSubVM(vmTypeName, vmInstanceId);

         // If still no view model instance, create it ourselves here.
         if (vmInstance == null)
         {
            var vmType = _vmTypes.FirstOrDefault(i => i.Name == vmTypeName);
            if (vmType == null)
               throw new Exception($"ERROR: '{vmId}' is not a known view model! Its assembly must be registered through VMController.RegisterAssembly.");

            try
            {
               if (vmInstanceId != null)
                  vmInstance = CreateInstance(vmType, new object[] { vmInstanceId }) as BaseVM;
            }
            catch (MissingMethodException)
            {
               Debug.Fail($"ERROR: '{vmTypeName}' has no constructor accepting instance ID.");
            }

            try
            {
               if (vmInstance == null)
                  vmInstance = CreateInstance(vmType, null) as BaseVM;
            }
            catch (MissingMethodException)
            {
               Debug.Fail($"ERROR: '{vmTypeName}' has no parameterless constructor.");
            }
         }

         // If there are view model arguments, set them into the instance.
         if (vmArg != null)
         {
            var vmJsonArg = (JObject)vmArg;
            foreach (var prop in vmJsonArg.Properties())
               UpdateVM(vmInstance, prop.Name, prop.Value.ToString());
         }

         return vmInstance;
      }

      /// <summary>
      /// Returns whether current security context is authorized to access a view model.
      /// </summary>
      /// <param name="vmInstance">View model instance.</param>
      /// <returns>True if authorized.</returns>
      protected virtual bool IsAuthorized(BaseVM vmInstance)
      {
         var authAttr = vmInstance.GetType().GetCustomAttribute<AuthorizeAttribute>();
         return authAttr == null || authAttr.IsAuthorized(Principal);
      }

      /// <summary>
      /// Updates a value of a view model.
      /// </summary>
      /// <param name="vmInstance">View model instance.</param>
      /// <param name="vmPath">View model property path.</param>
      /// <param name="newValue">New value.</param>
      protected virtual void UpdateVM(BaseVM vmInstance, string vmPath, string newValue)
      {
         try
         {
            object vmObject = vmInstance;
            var vmType = vmObject.GetType();
            var path = vmPath.Split('.');
            for (int i = 0; i < path.Length; i++)
            {
               var propName = path[i];
               var propInfo = vmType.GetProperty(propName);
               if (propInfo == null)
                  throw new UnresolvedVMUpdateException();

               if (i < path.Length - 1)
               {
                  // Path that starts with $ sign means it is a key to an IEnumerable property.
                  // By convention we expect a method whose name is in this format:
                  // <IEnumerable property name>_get (for example: ListContent_get) 
                  // to get the object whose key matches the given value in the path.
                  if (path[i + 1].StartsWith("$"))
                  {
                     var key = path[i + 1].TrimStart('$');
                     var methodInfo = vmType.GetMethod(propName + "_get");
                     if (methodInfo == null)
                        throw new UnresolvedVMUpdateException();

                     vmObject = methodInfo.Invoke(vmObject, new object[] { key });
                     if (vmObject == null)
                        throw new UnresolvedVMUpdateException();

                     vmType = vmObject.GetType();
                     i++;
                  }
                  else
                  {
                     vmObject = propInfo.GetValue(vmObject);
                     vmType = vmObject != null ? vmObject.GetType() : propInfo.PropertyType;
                  }
               }
               else if (typeof(ICommand).IsAssignableFrom(propInfo.PropertyType) && vmObject != null)
               {
                  // If the property type is ICommand, execute the command.
                  (propInfo.GetValue(vmObject) as ICommand)?.Execute(newValue);
               }
               else if (propInfo.SetMethod != null && vmObject != null)
               {
                  // Update the new value to the property.
                  if (propInfo.PropertyType.IsClass && propInfo.PropertyType != typeof(string))
                     propInfo.SetValue(vmObject, JsonConvert.DeserializeObject(newValue, propInfo.PropertyType));
                  else
                  {
                     var typeConverter = TypeDescriptor.GetConverter(propInfo.PropertyType);
                     if (typeConverter != null)
                        propInfo.SetValue(vmObject, typeConverter.ConvertFromString(newValue));
                  }

                  // Don't include the property we just updated in the ChangedProperties of the view model
                  // unless the value is changed internally, so that we don't send the same value back to the client
                  // during PushUpdates call by this VMController.
                  var changedProperties = vmInstance.ChangedProperties;
                  if (changedProperties.ContainsKey(vmPath) && (changedProperties[vmPath] ?? String.Empty).ToString() == newValue)
                  {
                     object value;
                     changedProperties.TryRemove(vmPath, out value);
                  }
               }
            }
         }
         catch (UnresolvedVMUpdateException)
         {
            // If we cannot resolve the property path, forward the info to the instance
            // to give it a chance to resolve it.
            vmInstance.OnUnresolvedUpdate(vmPath, newValue);
         }
      }

      /// <summary>
      /// Push property changed updates on all view models back to the client.
      /// </summary>
      protected virtual void PushUpdates()
      {
         foreach (var kvp in _activeVMs)
         {
            var vmInstance = kvp.Value.Instance;
            lock (vmInstance)
            {
               var changedProperties = new Dictionary<string, object>(vmInstance.ChangedProperties);
               if (changedProperties.Count > 0)
               {
                  var vmData = Serialize(changedProperties);
                  _vmResponse(kvp.Value.ConnectionId, kvp.Key, vmData);

                  // After the changes are forwarded, accept the changes so they won't be marked as changed anymore.
                  vmInstance.AcceptChangedProperties();
               }
            }
         }
      }

      /// <summary>
      /// Serializes an object.
      /// </summary>
      /// <param name="data">Data to serialize.</param>
      /// <returns>Serialized string.</returns>
      protected virtual string Serialize(object data)
      {
         List<string> ignoredPropertyNames = data is BaseVM ? (data as BaseVM).IgnoredProperties : null;
         return JsonConvert.SerializeObject(data, new JsonSerializerSettings { ContractResolver = new VMContractResolver(ignoredPropertyNames) });
      }

      /// <summary>
      /// Handles push updates request from a view model.
      /// </summary>
      private void VmInstance_RequestPushUpdates(object sender, EventArgs e)
      {
         PushUpdates();
      }
   }
}
