﻿using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orchard.DisplayManagement.Extensions;
using Orchard.Environment.Extensions;
using Orchard.Environment.Extensions.Features;
using Orchard.Environment.Extensions.Utility;
using Orchard.Events;
using Orchard.Utility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Orchard.DisplayManagement.Descriptors
{
    /// <summary>
    /// This class needs to be a singleton per tenant as it can contain different shapes
    /// for each tenant, even if they share the same theme.
    /// </summary>
    public class DefaultShapeTableManager : IShapeTableManager
    {
        private readonly IEnumerable<IShapeTableProvider> _bindingStrategies;
        private readonly IExtensionManager _extensionManager;
        private readonly IEventBus _eventBus;
        private readonly ITypeFeatureProvider _typeFeatureProvider;
        private readonly ILogger _logger;

        private readonly IMemoryCache _memoryCache;

        public DefaultShapeTableManager(
            IEnumerable<IShapeTableProvider> bindingStrategies,
            IExtensionManager extensionManager,
            IEventBus eventBus,
            ITypeFeatureProvider typeFeatureProvider,
            ILogger<DefaultShapeTableManager> logger,
            IMemoryCache memoryCache)
        {
            _bindingStrategies = bindingStrategies;
            _extensionManager = extensionManager;
            _eventBus = eventBus;
            _typeFeatureProvider = typeFeatureProvider;
            _logger = logger;
            _memoryCache = memoryCache;
        }

        public ShapeTable GetShapeTable(string themeId)
        {
            var cacheKey = $"ShapeTable:{themeId}";

            ShapeTable shapeTable;
            if (!_memoryCache.TryGetValue(cacheKey, out shapeTable))
            {

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Start building shape table");
                }
                IList<IReadOnlyList<ShapeAlteration>> alterationSets = new List<IReadOnlyList<ShapeAlteration>>();
                foreach (var bindingStrategy in _bindingStrategies)
                {
                    IFeatureInfo strategyDefaultFeature =
                        _typeFeatureProvider.GetFeatureForDependency(bindingStrategy.GetType());

                    var builder = new ShapeTableBuilder(strategyDefaultFeature);

                    bindingStrategy.Discover(builder);

                    var builtAlterations = builder.BuildAlterations().ToReadOnlyCollection();
                    if (builtAlterations.Any())
                    {
                        alterationSets.Add(builtAlterations);
                    }
                }

                var alterations = alterationSets
                    .SelectMany(shapeAlterations => shapeAlterations)
                    .Where(alteration => IsModuleOrRequestedTheme(alteration, themeId))
                    .OrderByDependenciesAndPriorities(AlterationHasDependency, GetPriority)
                    .ToList();

                var descriptors = alterations.GroupBy(alteration => alteration.ShapeType, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Aggregate(
                        new ShapeDescriptor { ShapeType = group.Key },
                        (descriptor, alteration) =>
                        {
                            alteration.Alter(descriptor);
                            return descriptor;
                        })).ToList();

                foreach (var descriptor in descriptors)
                {
                    foreach (var alteration in alterations.Where(a => a.ShapeType == descriptor.ShapeType).ToList())
                    {
                        var local = new ShapeDescriptor { ShapeType = descriptor.ShapeType };
                        alteration.Alter(local);
                        descriptor.BindingSources.Add(local.BindingSource);
                    }
                }

                shapeTable = new ShapeTable
                {
                    Descriptors = descriptors.ToDictionary(sd => sd.ShapeType, StringComparer.OrdinalIgnoreCase),
                    Bindings = descriptors.SelectMany(sd => sd.Bindings).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
                };

                //await _eventBus.NotifyAsync<IShapeTableEventHandler>(x => x.ShapeTableCreated(result));

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Done building shape table");
                }

                _memoryCache.Set(cacheKey, shapeTable, new MemoryCacheEntryOptions { Priority = CacheItemPriority.NeverRemove });
            }

            return shapeTable;
        }

        private static double GetPriority(ShapeAlteration shapeAlteration)
        {
            return shapeAlteration.Feature.Priority;
        }

        private bool AlterationHasDependency(ShapeAlteration item, ShapeAlteration subject)
        {
            return _extensionManager.ObserverHasADependencyOnSubject(item.Feature, subject.Feature);
        }

        private bool IsModuleOrRequestedTheme(ShapeAlteration alteration, string themeId)
        {
            if (alteration == null ||
                alteration.Feature == null)
            {
                return false;
            }

            if (string.IsNullOrEmpty(alteration.Feature.Extension.Manifest.Type))
            {
                // O2: The alteration must be coming from a library, e.g. Orchard.DisplayManagement
                return true;
            }

            if (alteration.Feature.Extension.Manifest.IsModule())
            {
                return true;
            }

            if (alteration.Feature.Extension.Manifest.IsTheme())
            {
                // A null theme means we are looking for any shape in any module or theme
                if (String.IsNullOrEmpty(themeId))
                {
                    return true;
                }

                // alterations from themes must be from the given theme or a base theme
                var featureId = alteration.Feature.Id;
                return string.IsNullOrEmpty(featureId) || featureId == themeId || IsBaseTheme(featureId, themeId);
            }

            return false;
        }

        private bool IsBaseTheme(string featureId, string themeId)
        {
            // determine if the given feature is a base theme of the given theme
            var availableFeatures = _extensionManager.GetExtensions().Features;

            var themeFeature = availableFeatures.SingleOrDefault(fd => fd.Id == themeId);
            while (themeFeature != null && themeFeature.Extension.Manifest.IsTheme())
            {
                var themeExtensionInfo = new ThemeExtensionInfo(themeFeature.Extension);
                if (!themeExtensionInfo.HasBaseTheme())
                {
                    return false;
                }
                if (themeExtensionInfo.IsBaseThemeFeature(featureId))
                {
                    return true;
                }
                themeFeature = availableFeatures.SingleOrDefault(fd => fd.Id == themeExtensionInfo.BaseTheme);
            }
            return false;
        }
    }
}