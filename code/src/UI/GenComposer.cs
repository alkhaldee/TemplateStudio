﻿// ******************************************************************
// Copyright (c) Microsoft. All rights reserved.
// This code is licensed under the MIT License (MIT).
// THE CODE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
// IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
// DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
// TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH
// THE CODE OR THE USE OR OTHER DEALINGS IN THE CODE.
// ******************************************************************

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.TemplateEngine.Abstractions;
using Microsoft.Templates.Core;
using Microsoft.Templates.Core.Gen;
using Microsoft.Templates.Core.Locations;
using Microsoft.Templates.Core.Composition;
using System.Collections.ObjectModel;
using Microsoft.Templates.UI.ViewModels;
using Microsoft.Templates.Core.Diagnostics;

namespace Microsoft.Templates.UI
{
    public class GenComposer
    {
        public static IEnumerable<string> GetSupportedFx(string projectType)
        {
            return GenContext.ToolBox.Repo.GetAll()
                .Where(t => t.GetProjectType() == projectType)
                .SelectMany(t => t.GetFrameworkList())
                .Distinct();
        }

        public static IEnumerable<(LayoutItem Layout, ITemplateInfo Template)> GetLayoutTemplates(string projectType, string framework)
        {
            var projectTemplate = GetProjectTemplate(projectType, framework);
            var layout = projectTemplate?.GetLayout();

            foreach (var item in layout)
            {
                var template = GenContext.ToolBox.Repo.Find(t => t.GroupIdentity == item.templateGroupIdentity && t.GetFrameworkList().Contains(framework));
                if (template == null)
                {
                    LogOrAlertException($"Layout template {item.templateGroupIdentity} not found for framework {framework}");
                }
                else
                {
                    var templateType = template.GetTemplateType();
                    //Only pages and features can be layout items
                    if (templateType != TemplateType.Page && templateType != TemplateType.Feature)
                    {
                        LogOrAlertException($"Invalid layout item {template.Identity}. Layout items must be of type Page or Feature.");
                    }
                    else
                    {
                        yield return (item, template);
                    }
                }
            }
        }

        public static IEnumerable<ITemplateInfo> GetAllDependencies(ITemplateInfo template, string framework)
        {
            var dependencyList = new List<ITemplateInfo>();
            return GetDependencies(template, framework, dependencyList);
            
        }

        private static IEnumerable<ITemplateInfo> GetDependencies(ITemplateInfo template, string framework, IList<ITemplateInfo> dependencyList)
        {
            var dependencies = template.GetDependencyList();

            foreach (var dependency in dependencies)
            {
                var dependencyTemplate =  GenContext.ToolBox.Repo.Find(t => t.Identity == dependency && t.GetFrameworkList().Contains(framework));
                if (dependencyTemplate == null)
                {
                    LogOrAlertException($"Dependency template {dependency} not found for framework {framework}");
                }
                else
                {
                    var templateType = dependencyTemplate?.GetTemplateType();
                    if (templateType != TemplateType.Page && templateType != TemplateType.Feature)
                    {
                        LogOrAlertException($"Invalid dependency item {dependencyTemplate.Identity}. Dependency items must be of type Page or Feature.");
                    }
                    else if (dependencyTemplate.GetMultipleInstance())
                    {
                        LogOrAlertException($"Invalid dependency item {dependencyTemplate.Identity}. Dependencies have to be configured as multpleInstance = false.");
                    }
                    else if (!dependencyList.Any(d => d.Identity == dependencyTemplate.Identity))
                    {
                        dependencyList.Add(dependencyTemplate);
                        GetDependencies(dependencyTemplate, framework, dependencyList);
                    }
                }
            }

            return dependencyList;
        }

        private static void LogOrAlertException(string message)
        {
#if DEBUG
            throw new ApplicationException(message);
#else
            //TODO: Log this
            AppHealth.Current.Warning.TrackAsync(message).FireAndForget();
#endif
        }

        public static IEnumerable<GenInfo> Compose(UserSelection userSelection)
        {
            var genQueue = new List<GenInfo>();

            if (string.IsNullOrEmpty(userSelection.ProjectType))
            {
                return genQueue;
            }

            AddProject(userSelection, genQueue);
            AddTemplates(userSelection.Pages, genQueue);
            AddTemplates(userSelection.Features, genQueue);

            AddCompositionTemplates(genQueue, userSelection);

            return genQueue;
        }

        private static void AddProject(UserSelection userSelection, List<GenInfo> genQueue)
        {
            var projectTemplate = GetProjectTemplate(userSelection.ProjectType, userSelection.Framework);

            var genProject = CreateGenInfo(GenContext.Current.ProjectName, projectTemplate, genQueue);
            genProject.Parameters.Add(GenParams.Username, Environment.UserName);
            genProject.Parameters.Add(GenParams.WizardVersion, GenContext.GetWizardVersion());
            genProject.Parameters.Add(GenParams.TemplatesVersion, GenContext.ToolBox.Repo.GetTemplatesVersion());
        }

        private static ITemplateInfo GetProjectTemplate(string projectType, string framework)
        {
            return GenContext.ToolBox.Repo
                                    .Find(t => t.GetTemplateType() == TemplateType.Project
                                            && t.GetProjectType() == projectType
                                            && t.GetFrameworkList().Any(f => f == framework));
        }

        private static void AddTemplates(IEnumerable<(string name, ITemplateInfo template)> userSelection, List<GenInfo> genQueue)
        {
            foreach (var selectionItem in userSelection)
            {
                CreateGenInfo(selectionItem.name, selectionItem.template, genQueue);
            }
        }

        public static void AddMissingLicences(ITemplateInfo template, ObservableCollection<SummaryLicenceViewModel> summaryLicences)
        {
            var licences = template.GetLicences();
            foreach (var licence in licences)
            {
                if (!summaryLicences.Any(st => st.Text == licence.text))
                {
                    summaryLicences.Add(new SummaryLicenceViewModel()
                    {
                        Text = licence.text,
                        Url = licence.url
                    });
                }
            }
        }

        private static void AddCompositionTemplates(List<GenInfo> genQueue, UserSelection userSelection)
        {
            var compositionCatalog = GetCompositionCatalog().ToList();

            var context = new QueryablePropertyDictionary();

            context.Add(new QueryableProperty("projectType", userSelection.ProjectType));
            context.Add(new QueryableProperty("framework", userSelection.Framework));

            var compositionQueue = new List<GenInfo>();

            foreach (var genItem in genQueue)
            {
                foreach (var compositionItem in compositionCatalog)
                {
                    if (compositionItem.query.Match(genItem.Template, context))
                    {
                        AddTemplate(genItem, compositionQueue, compositionItem.template);
                    }

                }
            }

            genQueue.AddRange(compositionQueue);
        }

        private static IEnumerable<(CompositionQuery query, ITemplateInfo template)> GetCompositionCatalog()
        {
            return GenContext.ToolBox.Repo
                                        .Get(t => t.GetTemplateType() == TemplateType.Composition)
                                        .Select(t => (CompositionQuery.Parse(t.GetCompositionFilter()), t))
                                        .ToList();
        }

        private static void AddTemplate(GenInfo mainGenInfo, List<GenInfo> queue, ITemplateInfo targetTemplate)
        {
            if (targetTemplate != null)
            {
                foreach (var export in targetTemplate.GetExports())
                {
                    mainGenInfo.Parameters.Add(export.name, export.value);
                }

                CreateGenInfo(mainGenInfo.Name, targetTemplate, queue);
            }
        }

        private static GenInfo CreateGenInfo(string name, ITemplateInfo template, List<GenInfo> queue)
        {
            var genInfo = new GenInfo
            {
                Name = name,
                Template = template
            };
            queue.Add(genInfo);

            AddDefaultParams(genInfo);

            return genInfo;
        }

        private static void AddDefaultParams(GenInfo genInfo)
        {
            var ns = GenContext.ToolBox.Shell.GetActiveNamespace();
            if (string.IsNullOrEmpty(ns))
            {
                ns = GenContext.Current.ProjectName;
            }
            genInfo.Parameters.Add(GenParams.RootNamespace, ns);

            //TODO: THIS SHOULD BE THE ITEM IN CONTEXT
            genInfo.Parameters.Add(GenParams.ItemNamespace, ns);
        }
    }
}
