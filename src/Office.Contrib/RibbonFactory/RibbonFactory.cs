﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Office.Core;
using Microsoft.Office.Tools;

namespace Office.Contrib.RibbonFactory
{
    /// <summary>
    /// Simplifies adding custom Ribbon's to Office. 
    /// Allows the custom Ribbon xml to be wired up to IRibbonViewModel's
    /// by convention. Simply name the Ribbon.xml the same as the ribbon view model class
    /// in the same assembly
    /// </summary>
    [ComVisible(true)]
    public abstract partial class RibbonFactory<TRibbonTypes> : IRibbonFactory
        where TRibbonTypes : struct
    {
        private IViewProvider<TRibbonTypes> _viewProvider;

        /// <summary>
        /// Lookup from a viewmodel type to it's ribbon XML
        /// </summary>
        private readonly Dictionary<TRibbonTypes, string> _ribbonViews = new Dictionary<TRibbonTypes, string>();
        private readonly Dictionary<string, CallbackTarget<TRibbonTypes>> _ribbonCallbackTarget =
            new Dictionary<string, CallbackTarget<TRibbonTypes>>();
        const string OfficeCustomui = "http://schemas.microsoft.com/office/2006/01/customui";
        const string OfficeCustomui4 = "http://schemas.microsoft.com/office/2009/07/customui";
        internal const string CommonCallbacks = "CommonCallbacks";
        private ControlCallbackLookup<TRibbonTypes> _controlCallbackLookup;
        private IViewLocationStrategy _viewLocationStrategy;
        
        private bool _initialsed;
        private readonly RibbonViewModelHelper _ribbonViewModelHelper = new RibbonViewModelHelper();
        private ViewModelResolver<TRibbonTypes> _ribbonViewModelResolver;
        private static readonly object InstanceLock = new object();

        /// <summary>
        /// Initializes a new instance of the <see cref="RibbonFactory&lt;TRibbonTypes&gt;"/> class.
        /// </summary>
        /// <param name="viewLocationStrategy">The view location strategy, null for default strategy.</param>
        protected RibbonFactory(IViewLocationStrategy viewLocationStrategy = null)
        {
            lock (InstanceLock)
            {
                if (Current != null)
                    throw new InvalidOperationException("You can only create a single ribbon factory");
                Current = this;
            }
            
            _viewLocationStrategy = viewLocationStrategy ?? new DefaultViewLocationStrategy();
        }

        /// <summary>
        /// Initialises and builds up the ribbon factory
        /// </summary>
        /// <param name="ribbonFactory">The ribbon factory.</param>
        /// <param name="customTaskPaneCollection">The custom task pane collection.</param>
        /// <param name="assemblies">The assemblies to scan for view models.</param>
        /// <returns>
        /// Disposible object to call on outlook shutdown
        /// </returns>
        /// <exception cref="ViewNotFoundException">If the view cannot be located for a view model</exception>
        public virtual IDisposable InitialiseFactory(
            Func<Type, IRibbonViewModel> ribbonFactory,  
            CustomTaskPaneCollection customTaskPaneCollection, 
            params Assembly[] assemblies)
        {
            if (assemblies.Length == 0) 
                throw new InvalidOperationException("You must specify at least one assembly to scan for viewmodels");
            if (_initialsed)
                throw new InvalidOperationException("Ribbon Factory already Initialised");

            _initialsed = true;

            _viewProvider = ViewProvider();

            var ribbonTypes = GetTRibbonTypessInAssemblies(assemblies).ToList();

            _ribbonViewModelResolver = new ViewModelResolver<TRibbonTypes>(
                ribbonTypes, ribbonFactory, _ribbonViewModelHelper, customTaskPaneCollection, _viewProvider);
            _controlCallbackLookup = new ControlCallbackLookup<TRibbonTypes>(GetRibbonElements());

            Expression<Action> loadMethod = () => Ribbon_Load(null);
            var loadMethodName = loadMethod.GetMethodName();

            foreach (var viewModelType in ribbonTypes)
            {
                LocateAndRegisterViewXml(viewModelType, loadMethodName);
            }

            _viewProvider.Initialise();

            return _ribbonViewModelResolver;
        }

        /// <summary>
        /// Provider which tells the ribbon factory when a new view is opened, and needs to be wired up
        /// </summary>
        /// <returns>An instance of the view provider</returns>
        protected abstract IViewProvider<TRibbonTypes> ViewProvider();

        private static IEnumerable<Type> GetTRibbonTypessInAssemblies(IEnumerable<Assembly> assemblies)
        {
            var ribbonViewModelType = typeof (IRibbonViewModel);
            return assemblies
                .Select(
                    assembly =>
                        {
                            var types = assembly.GetTypes();
                            return types.Where(ribbonViewModelType.IsAssignableFrom);
                        }
                )
                .Aggregate((t, t1) => t.Concat(t1));
        }

        private void LocateAndRegisterViewXml(Type viewModelType, string loadMethodName)
        {
            var resourceText = (string)_viewLocationStrategy.GetType()
                    .GetMethod("LocateViewForViewModel")
                    .MakeGenericMethod(viewModelType)
                    .Invoke(_viewLocationStrategy, new object[] { });

            var ribbonDoc = XDocument.Parse(resourceText);

            //We have to override the Ribbon_Load event to make sure we get the callback
            var customUi = 
                ribbonDoc.Descendants(XName.Get("customUI", OfficeCustomui)).SingleOrDefault()
                ?? ribbonDoc.Descendants(XName.Get("customUI", OfficeCustomui4)).Single();

            customUi.SetAttributeValue("onLoad", loadMethodName);

            foreach (var value in _ribbonViewModelHelper.GetRibbonTypesFor<TRibbonTypes>(viewModelType))
            {
                WireUpEvents(value, ribbonDoc, customUi.GetDefaultNamespace());
                _ribbonViews.Add(value, ribbonDoc.ToString());
            }
        }

        ///<summary>
        /// Gets or Sets the strategy that fetches the Ribbon XML for a given view
        ///</summary>
        public IViewLocationStrategy LocateViewStrategy
        {
            get { return _viewLocationStrategy; }
            set
            {
                if (value == null) return;

                _viewLocationStrategy = value;
            }
        }

        /// <summary>
        /// Current instance of RibbonFactory
        /// </summary>
        public static IRibbonFactory Current { get; protected set; }

        /// <summary>
        /// Ribbon_s the load.
        /// </summary>
        /// <param name="ribbonUi">The ribbon UI.</param>
        public void Ribbon_Load(IRibbonUI ribbonUi)
        {
            _ribbonViewModelResolver.RibbonLoaded(ribbonUi);
        }

        private void WireUpEvents(TRibbonTypes ribbonTypes, XContainer ribbonDoc, XNamespace xNamespace)
        {
            //Go through each type of Ribbon 
            foreach (var ribbonControl in _controlCallbackLookup.RibbonControls)
            {
                //Get each instance of that control in the ribbon definition file
                var xElements = ribbonDoc.Descendants(XName.Get(ribbonControl, xNamespace.NamespaceName));

                foreach (var xElement in xElements)
                {
                    var elementId = xElement.Attribute(XName.Get("id"));
                    if (elementId == null) continue;

                    //Go through each possible callback, Concat with common methods on all controls
                    foreach (var controlCallback in _controlCallbackLookup.GetVstoControlCallbacks(ribbonControl))
                    {
                        //Look for a defined callback
                        var callbackAttribute = xElement.Attribute(XName.Get(controlCallback));

                        if (callbackAttribute == null) continue;
                        var currentCallback = callbackAttribute.Value;
                        //Set the callback value to the callback method defined on this factory
                        var factoryMethodName = _controlCallbackLookup.GetFactoryMethodName(ribbonControl, controlCallback);
                        callbackAttribute.SetValue(factoryMethodName);

                        //Set the tag attribute of the element, this is needed to know where to 
                        // direct the callback
                        var callbackTag = BuildTag(ribbonTypes, elementId, factoryMethodName);
                        _ribbonCallbackTarget.Add(callbackTag, new CallbackTarget<TRibbonTypes>(ribbonTypes, currentCallback));
                        xElement.SetAttributeValue(XName.Get("tag"), (ribbonTypes + elementId.Value));
                        _ribbonViewModelResolver.RegisterCallbackControl(ribbonTypes, currentCallback, elementId.Value);
                    }
                }
            }
        }

        private static string BuildTag(TRibbonTypes viewModelType, XAttribute elementId, string factoryMethodName)
        {
            return viewModelType + elementId.Value + factoryMethodName;
        }

        /// <summary>
        /// Gets the custom UI.
        /// </summary>
        /// <param name="ribbonId">The ribbon id.</param>
        /// <returns></returns>
        public string GetCustomUI(string ribbonId)
        {
            TRibbonTypes enumFromDescription;
            try
            {
                enumFromDescription = EnumExtensions.EnumFromDescription<TRibbonTypes>(ribbonId);
            }
            catch (ArgumentException)
            {
                //An unknown ribbon type
                return null;
            }

            return !_ribbonViews.ContainsKey(enumFromDescription) 
                ? null 
                : _ribbonViews[enumFromDescription];
        }

        private object InvokeGet(IRibbonControl control, Expression<Action> caller, params object[] parameters)
        {
            var callbackTarget = _ribbonCallbackTarget[control.Tag + caller.GetMethodName()];

            var viewModelInstance = _ribbonViewModelResolver.ResolveInstanceFor(control.Context);

            Type type = viewModelInstance.GetType();
            var property = type.GetProperty(callbackTarget.Method);

            if (property != null)
            {
                return type.InvokeMember(callbackTarget.Method,
                                         BindingFlags.GetProperty,
                                         null,
                                         viewModelInstance,
                                         null);
            }

            try
            {
                return type.InvokeMember(callbackTarget.Method,
                                     BindingFlags.InvokeMethod,
                                     null,
                                     viewModelInstance,
                                     new[]
                                         {
                                             control
                                         }
                                         .Concat(parameters)
                                         .ToArray());
            }
            catch (MissingMethodException)
            {
                throw new InvalidOperationException(
                    string.Format("Expecting method with signature: {0}.{1}(IRibbonControl control)",
                    type.Name,
                    callbackTarget.Method));
            }
            
        }

        private void Invoke(IRibbonControl control, Expression<Action> caller, params object[] parameters)
        {
            var callbackTarget = _ribbonCallbackTarget[control.Tag+caller.GetMethodName()];

            var viewModelInstance = _ribbonViewModelResolver.ResolveInstanceFor(control.Context);

            Type type = viewModelInstance.GetType();
            var property = type.GetProperty(callbackTarget.Method);

            if (property != null)
            {
                type.InvokeMember(callbackTarget.Method,
                                       BindingFlags.SetProperty,
                                       null,
                                       viewModelInstance,
                                       new[]
                                           {
                                               parameters.Single()
                                           });
            }
            else
            {
                type.InvokeMember(callbackTarget.Method,
                                       BindingFlags.InvokeMethod,
                                       null,
                                       viewModelInstance,
                                       new[]
                                           {
                                               control
                                           }
                                       .Concat(parameters)
                                       .ToArray());
            }
        }
    }
}