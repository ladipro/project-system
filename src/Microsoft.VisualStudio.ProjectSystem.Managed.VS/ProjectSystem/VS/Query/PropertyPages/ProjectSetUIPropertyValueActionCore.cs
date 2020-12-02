﻿// Licensed to the .NET Foundation under one or more agreements. The .NET Foundation licenses this file to you under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Build.Framework.XamlTypes;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using Microsoft.VisualStudio.ProjectSystem.Query;

namespace Microsoft.VisualStudio.ProjectSystem.VS.Query
{
    /// <summary>
    /// <para>
    /// Handles the core logic of setting properties on projects. Note this type has no dependencies on the Project Query API;
    /// extracting the necessary data from the API is handled by <see cref="ProjectSetUIPropertyValueActionBase{T}"/>.
    /// </para>
    /// <para>
    /// This handles setting a specific property on a specific page across multiple configurations of multiple projects.
    /// </para>
    /// </summary>
    internal class ProjectSetUIPropertyValueActionCore
    {
        private readonly IPropertyPageQueryCacheProvider _queryCacheProvider;
        private readonly string _pageName;
        private readonly string _propertyName;
        private readonly IEnumerable<(string dimension, string value)> _dimensions;
        private readonly Func<IProperty, Task> _setValueAsync;

        private readonly Dictionary<string, List<IRule>> _rules = new(StringComparers.Paths);

        /// <summary>
        /// Creates a <see cref="ProjectSetUIPropertyValueActionCore"/>.
        /// </summary>
        /// <param name="queryCacheProvider">Provides access to a <see cref="UnconfiguredProject"/>'s known configurations and <see cref="IRule"/>s.</param>
        /// <param name="pageName">The name of the page containing the property.</param>
        /// <param name="propertyName">The name of the property to update.</param>
        /// <param name="dimensions">The dimension names and values indicating which project configurations should be updated with the new value.</param>
        /// <param name="setValueAsync">A delegate that, given the <see cref="IProperty"/> to update, actually sets the value.</param>
        public ProjectSetUIPropertyValueActionCore(
            IPropertyPageQueryCacheProvider queryCacheProvider,
            string pageName,
            string propertyName,
            IEnumerable<(string dimension, string value)> dimensions,
            Func<IProperty, Task> setValueAsync)
        {
            _queryCacheProvider = queryCacheProvider;
            _pageName = pageName;
            _propertyName = propertyName;
            _dimensions = dimensions;
            _setValueAsync = setValueAsync;
        }

        /// <summary>
        /// Handles any pre-processing that should occur before actually setting the property values.
        /// This is called once before <see cref="ExecuteAsync(UnconfiguredProject)"/>.
        /// </summary>
        /// <remarks>
        /// Because of the project locks help by the core parts of the Project Query API in CPS we need
        /// to retrieve and cache all of the affected <see cref="IRule"/>s ahead of time. 
        /// </remarks>
        /// <param name="targetProjects">The set of projects we should try to update.</param>
        public async Task OnBeforeExecutingBatchAsync(IEnumerable<UnconfiguredProject> targetProjects)
        {
            foreach (UnconfiguredProject project in targetProjects)
            {
                if (!_rules.TryGetValue(project.FullPath, out List<IRule> projectRules)
                    && await project.GetProjectLevelPropertyPagesCatalogAsync() is IPropertyPagesCatalog projectCatalog
                    && projectCatalog.GetSchema(_pageName) is Rule rule
                    && rule.GetProperty(_propertyName) is BaseProperty property)
                {
                    bool configurationDependent = property.IsConfigurationDependent();
                    projectRules = new List<IRule>();
                    IPropertyPageQueryCache propertyPageCache = _queryCacheProvider.CreateCache(project);
                    if (configurationDependent)
                    {
                        // The property is configuration-dependent; we need to collect the bound rules for
                        // all matching configurations.
                        if (await propertyPageCache.GetKnownConfigurationsAsync() is IImmutableSet<ProjectConfiguration> knownConfigurations)
                        {
                            foreach (ProjectConfiguration knownConfiguration in knownConfigurations.Where(config => config.MatchesDimensions(_dimensions)))
                            {
                                if (await propertyPageCache.BindToRule(knownConfiguration, _pageName) is IRule boundRule)
                                {
                                    projectRules.Add(boundRule);
                                }
                            }
                        }
                    }
                    else
                    {
                        // The property is configuration-independent; we only need the bound rule for a single
                        // configuration.
                        if (await propertyPageCache.GetSuggestedConfigurationAsync() is ProjectConfiguration suggestedConfiguration
                            && await propertyPageCache.BindToRule(suggestedConfiguration, _pageName) is IRule boundRule)
                        {
                            projectRules.Add(boundRule);
                        }
                    }

                    _rules.Add(project.FullPath, projectRules);
                }
            }
        }

        /// <summary>
        /// Handles setting the property value within a single project. This is called once per
        /// <see cref="UnconfiguredProject"/> targeted by the query.
        /// </summary>
        /// <param name="targetProject">The project to update.</param>
        public async Task ExecuteAsync(UnconfiguredProject targetProject)
        {
            if (_rules.TryGetValue(targetProject.FullPath, out List<IRule> boundRules))
            {
                foreach (IRule boundRule in boundRules)
                {
                    if (boundRule.GetProperty(_propertyName) is IProperty property)
                    {
                        await _setValueAsync(property);
                    }
                }
            }
        }

        /// <summary>
        /// Handles clean up when we're all done executing the project action. This is called
        /// once after all calls to <see cref="ExecuteAsync(UnconfiguredProject)"/> have completed.
        /// </summary>
        public void OnAfterExecutingBatch()
        {
            _rules.Clear();
        }
    }
}
