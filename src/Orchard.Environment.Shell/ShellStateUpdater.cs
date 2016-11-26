﻿using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using Orchard.DeferredTasks;
using Orchard.Environment.Extensions;
using Orchard.Environment.Shell.State;
using Orchard.Events;
using Orchard.Environment.Extensions.Features;
using Orchard.Environment.Extensions.Manifests;
using System.Threading.Tasks;
using Orchard.Environment.Extensions.Utility;

namespace Orchard.Environment.Shell
{
    public interface IShellStateUpdater
    {
        Task ApplyChanges();
    }

    public class ShellStateUpdater : IShellStateUpdater
    {
        private readonly ShellSettings _settings;
        private readonly IShellStateManager _stateManager;
        private readonly IExtensionManager _extensionManager;
        private readonly IEventBus _eventBus;
        private readonly IDeferredTaskEngine _deferredTaskEngine;

        public ShellStateUpdater(
            ShellSettings settings,
            IShellStateManager stateManager,
            IExtensionManager extensionManager,
            IEventBus eventBus,
            IDeferredTaskEngine deferredTaskEngine,
            ILogger<ShellStateCoordinator> logger)
        {
            _deferredTaskEngine = deferredTaskEngine;
            _settings = settings;
            _stateManager = stateManager;
            _extensionManager = extensionManager;
            _eventBus = eventBus;
            Logger = logger;
        }

        public ILogger Logger { get; set; }

        public async Task ApplyChanges()
        {
            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation("Applying changes for for shell '{0}'", _settings.Name);
            }

            var shellState = await _stateManager.GetShellStateAsync();

            // start with description of all declared features in order - order preserved with all merging
            var allUnorderedFeatures = _extensionManager.GetExtensions().Features.ToArray();
            var orderedFeatureDescriptors = allUnorderedFeatures
                .OrderByDependenciesAndPriorities(
                    (fiObv, fiSub) => _extensionManager.GetDependentFeatures(fiObv.Id, allUnorderedFeatures).Contains(fiSub), 
                    (fi2) => fi2.Priority);

            // merge feature state into ordered list
            var orderedFeatureDescriptorsAndStates = orderedFeatureDescriptors
                .Select(featureInfo => new
                {
                    FeatureDescriptor = featureInfo,
                    FeatureState = shellState.Features.FirstOrDefault(s => s.Id == featureInfo.Id),
                })
                .Where(entry => entry.FeatureState != null)
                .ToArray();

            // get loaded feature information
            var loadedFeatures = await Task.WhenAll(orderedFeatureDescriptorsAndStates
                .Select(x => _extensionManager.LoadFeatureAsync(x.FeatureDescriptor))
                .ToArray());
            
            // merge loaded feature information into ordered list
            var loadedEntries = orderedFeatureDescriptorsAndStates.Select(
                entry => new
                {
                    Feature = loadedFeatures.SingleOrDefault(f => f.FeatureInfo == entry.FeatureDescriptor)
                              ?? new NonCompiledFeatureEntry(entry.FeatureDescriptor),
                    entry.FeatureDescriptor,
                    entry.FeatureState,
                }).ToList();

            // find feature state that is beyond what's currently available from modules
            var additionalState = shellState.Features.Except(loadedEntries.Select(entry => entry.FeatureState));

            // create additional stub entries for the sake of firing state change events on missing features
            var allEntries = loadedEntries.Concat(additionalState.Select(featureState =>
            {
                var featureDescriptor = new InternalFeatureInfo(
                    featureState.Id,
                    new InternalExtensionInfo(featureState.Id)
                    );
                return new
                {
                    Feature = (FeatureEntry)new NonCompiledFeatureEntry(featureDescriptor),
                    FeatureDescriptor = (IFeatureInfo)featureDescriptor,
                    FeatureState = featureState
                };
            })).ToArray();

            // lower enabled states in reverse order
            foreach (var entry in allEntries.Reverse().Where(entry => entry.FeatureState.EnableState == ShellFeatureState.State.Falling))
            {
                if (Logger.IsEnabled(LogLevel.Information))
                {
                    Logger.LogInformation("Disabling feature '{0}'", entry.Feature.FeatureInfo.Id);
                }

                _eventBus.Notify<IFeatureEventHandler>(x => x.Disabling(entry.Feature.FeatureInfo));
                await _stateManager.UpdateEnabledStateAsync(entry.FeatureState, ShellFeatureState.State.Down);
                _eventBus.Notify<IFeatureEventHandler>(x => x.Disabled(entry.Feature.FeatureInfo));
            }

            // lower installed states in reverse order
            foreach (var entry in allEntries.Reverse().Where(entry => entry.FeatureState.InstallState == ShellFeatureState.State.Falling))
            {
                if (Logger.IsEnabled(LogLevel.Information))
                {
                    Logger.LogInformation("Uninstalling feature '{0}'", entry.Feature.FeatureInfo.Id);
                }

                _eventBus.Notify<IFeatureEventHandler>(x => x.Uninstalling(entry.Feature.FeatureInfo));
                await _stateManager.UpdateInstalledStateAsync(entry.FeatureState, ShellFeatureState.State.Down);
                _eventBus.Notify<IFeatureEventHandler>(x => x.Uninstalled(entry.Feature.FeatureInfo));
            }

            // raise install and enabled states in order
            foreach (var entry in allEntries.Where(entry => IsRising(entry.FeatureState)))
            {
                if (entry.FeatureState.InstallState == ShellFeatureState.State.Rising)
                {
                    if (Logger.IsEnabled(LogLevel.Information))
                    {
                        Logger.LogInformation("Installing feature '{0}'", entry.Feature.FeatureInfo.Id);
                    }

                    _eventBus.Notify<IFeatureEventHandler>(x => x.Installing(entry.Feature.FeatureInfo));
                    await _stateManager.UpdateInstalledStateAsync(entry.FeatureState, ShellFeatureState.State.Up);
                    _eventBus.Notify<IFeatureEventHandler>(x => x.Installed(entry.Feature.FeatureInfo));
                }
                if (entry.FeatureState.EnableState == ShellFeatureState.State.Rising)
                {
                    if (Logger.IsEnabled(LogLevel.Information))
                    {
                        Logger.LogInformation("Enabling feature '{0}'", entry.Feature.FeatureInfo.Id);
                    }

                    _eventBus.Notify<IFeatureEventHandler>(x => x.Enabling(entry.Feature.FeatureInfo));
                    await _stateManager.UpdateEnabledStateAsync(entry.FeatureState, ShellFeatureState.State.Up);
                    _eventBus.Notify<IFeatureEventHandler>(x => x.Enabled(entry.Feature.FeatureInfo));
                }
            }
        }

        static bool IsRising(ShellFeatureState state)
        {
            return state.InstallState == ShellFeatureState.State.Rising ||
                   state.EnableState == ShellFeatureState.State.Rising;
        }
    }
}
