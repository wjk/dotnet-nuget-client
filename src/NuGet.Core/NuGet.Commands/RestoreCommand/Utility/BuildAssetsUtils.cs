// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using NuGet.Client;
using NuGet.Common;
using NuGet.ContentModel;
using NuGet.DependencyResolver;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Repositories;
using NuGet.Versioning;

namespace NuGet.Commands
{
    public static class BuildAssetsUtils
    {
        private static readonly XNamespace Namespace = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
        internal static readonly string CrossTargetingCondition = "'$(TargetFramework)' == ''";
        internal static readonly string TargetFrameworkCondition = "'$(TargetFramework)' == '{0}'";
        internal static readonly string LanguageCondition = "'$(Language)' == '{0}'";
        internal static readonly string ExcludeAllCondition = "'$(ExcludeRestorePackageImports)' != 'true'";
        private const string TargetsExtension = ".targets";
        private const string PropsExtension = ".props";

        /// <summary>
        /// The macros that we may use in MSBuild to replace path roots.
        /// </summary>
        public static readonly string[] MacroCandidates = new[]
        {
            "UserProfile", // e.g. C:\users\myusername
        };

        /// <summary>
        /// Write XML to disk.
        /// Delete files which do not have new XML.
        /// </summary>
        public static void WriteFiles(IEnumerable<MSBuildOutputFile> files, ILogger log)
        {
            foreach (var file in files)
            {
                if (file.Content == null)
                {
                    // Remove the file if the XML is null
                    FileUtility.Delete(file.Path);
                }
                else
                {
                    log.LogMinimal(string.Format(CultureInfo.CurrentCulture, Strings.Log_GeneratingMsBuildFile, file.Path));

                    // Create the directory if it doesn't exist
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Path));

                    // Write out XML file
                    WriteXML(file.Path, file.Content);
                }
            }
        }

        /// <summary>
        /// Create MSBuild targets and props files.
        /// Null will be returned for files that should be removed.
        /// </summary>
        public static List<MSBuildOutputFile> GenerateMultiTargetFailureFiles(
            string targetsPath,
            string propsPath,
            string repositoryRoot,
            bool success,
            RestoreOutputType restoreType,
            ILogger log,
            CancellationToken token)
        {
            XDocument targetsXML = null;
            XDocument propsXML = null;

            // Create an error file for MSBuild to stop the build.
            targetsXML = GenerateMultiTargetFrameworkWarning(repositoryRoot, restoreType, success);

            if (restoreType == RestoreOutputType.NETCore)
            {
                propsXML = GenerateEmptyImportsFile(repositoryRoot, restoreType, success);
            }

            return new List<MSBuildOutputFile>()
            {
                new MSBuildOutputFile(targetsPath, targetsXML),
                new MSBuildOutputFile(propsPath, propsXML),
            };
        }

        /// <summary>
        /// Create MSBuild targets and props files.
        /// Null will be returned for files that should be removed.
        /// </summary>
        public static IReadOnlyList<MSBuildOutputFile> GenerateFiles(
            string targetsPath,
            string propsPath,
            List<MSBuildRestoreItemGroup> targets,
            List<MSBuildRestoreItemGroup> props,
            string repositoryRoot,
            bool success,
            RestoreOutputType restoreType,
            ILogger log)
        {
            XDocument targetsXML = null;
            XDocument propsXML = null;

            // Generate the files as needed for project.json
            // Always generate for NETCore
            if (restoreType == RestoreOutputType.NETCore
                || targets.Any(group => group.Items.Count > 0))
            {
                targetsXML = GenerateMSBuildFile(targets, repositoryRoot, restoreType, success);
            }

            if (restoreType == RestoreOutputType.NETCore
                || props.Any(group => group.Items.Count > 0))
            {
                propsXML = GenerateMSBuildFile(props, repositoryRoot, restoreType, success);
            }

            return new List<MSBuildOutputFile>()
            {
                new MSBuildOutputFile(targetsPath, targetsXML),
                new MSBuildOutputFile(propsPath, propsXML),
            };
        }

        public static string ReplacePathsWithMacros(string path)
        {
            foreach (var macroName in MacroCandidates)
            {
                string macroValue = Environment.GetEnvironmentVariable(macroName);
                if (!string.IsNullOrEmpty(macroValue)
                    && path.StartsWith(macroValue, StringComparison.OrdinalIgnoreCase))
                {
                    path = $"$({macroName})" + $"{path.Substring(macroValue.Length)}";
                }

                break;
            }

            return path;
        }

        public static XDocument GenerateMultiTargetFrameworkWarning(string repositoryRoot, RestoreOutputType outputType, bool success)
        {
            var doc = GenerateEmptyImportsFile(repositoryRoot, outputType, success);
            var ns = doc.Root.GetDefaultNamespace();

            doc.Root.Add(new XElement(ns + "Target",
                        new XAttribute("Name", "EmitMSBuildWarning"),
                        new XAttribute("BeforeTargets", "Build"),

                        new XElement(ns + "Warning",
                            new XAttribute("Text", Strings.MSBuildWarning_MultiTarget))));

            return doc;
        }

        /// <summary>
        /// Get empty file with the base properties.
        /// </summary>
        public static XDocument GenerateEmptyImportsFile(string repositoryRoot, RestoreOutputType outputType, bool success)
        {
            var projectStyle = "Unknown";

            if (outputType == RestoreOutputType.NETCore)
            {
                projectStyle = "PackageReference";
            }
            else if (outputType == RestoreOutputType.UAP)
            {
                projectStyle = "ProjectJson";
            }

            var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", "no"),

                new XElement(ns + "Project",
                    new XAttribute("ToolsVersion", "14.0"),

                    new XElement(ns + "PropertyGroup",
                        GetProperty("NuGetPackageRoot", ReplacePathsWithMacros(repositoryRoot)),
                        GetProperty("NuGetProjectStyle", projectStyle),
                        GetProperty("NuGetToolVersion", MinClientVersionUtility.GetNuGetClientVersion().ToNormalizedString()),
                        GetProperty("NuGetRestoreSuccess", success.ToString()))));

            return doc;
        }

        public static XElement GetProperty(string propertyName, string content)
        {
            return new XElement(Namespace + propertyName,
                            new XAttribute("Condition", $" '$({propertyName})' == '' "),
                            content);
        }

        public static XElement GenerateImport(string path)
        {
            return new XElement(Namespace + "Import",
                                new XAttribute("Project", path),
                                new XAttribute("Condition", $"Exists('{path}')"));
        }

        public static XElement GenerateContentFilesItem(string path, LockFileContentFile item)
        {
            // TODO: add other attributes
            return new XElement(Namespace + item.BuildAction.Value,
                                new XAttribute("Include", path),
                                new XAttribute("Condition", $"Exists('{path}')"));
        }

        /// <summary>
        /// Returns null if the result should not exist on disk.
        /// </summary>
        public static XDocument GenerateMSBuildFile(List<MSBuildRestoreItemGroup> groups,
            string repositoryRoot,
            RestoreOutputType outputType,
            bool success)
        {
            XDocument doc = null;

            // Always write out netcore props/targets. For project.json only write the file if it has items.
            if (outputType == RestoreOutputType.NETCore || groups.SelectMany(e => e.Items).Any())
            {
                doc = GenerateEmptyImportsFile(repositoryRoot, outputType, success);

                // Add import groups, order by position, then by the conditions to keep the results deterministic
                // Skip empty groups
                foreach (var group in groups
                    .Where(e => e.Items.Count > 0)
                    .OrderBy(e => e.Position)
                    .ThenBy(e => e.Condition, StringComparer.OrdinalIgnoreCase))
                {
                    var itemGroup = new XElement(Namespace + "ImportGroup", group.Items);

                    // Add a conditional statement if multiple TFMs exist or cross targeting is present
                    var conditionValue = group.Condition;
                    if (!string.IsNullOrEmpty(conditionValue))
                    {
                        itemGroup.Add(new XAttribute("Condition", conditionValue));
                    }

                    // Add itemgroup to file
                    doc.Root.Add(itemGroup);
                }
            }

            return doc;
        }

        public static void WriteXML(string path, XDocument doc)
        {
            FileUtility.Replace((outputPath) =>
            {
                using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    doc.Save(output);
                }
            },
            path);
        }

        public static string GetImportPath(string importPath, string repositoryRoot)
        {
            var path = importPath;

            if (importPath.StartsWith(repositoryRoot, StringComparison.Ordinal))
            {
                path = $"$(NuGetPackageRoot){importPath.Substring(repositoryRoot.Length)}";
            }
            else
            {
                path = ReplacePathsWithMacros(importPath);
            }

            return path;
        }

        /// <summary>
        /// Check if the file has changes compared to the original on disk.
        /// </summary>
        public static bool HasChanges(XDocument newFile, string path, ILogger log)
        {
            XDocument existing = ReadExisting(path, log);

            if (existing != null)
            {
                // Use a simple string compare to check if the files match
                // This can be optimized in the future, but generally these are very small files.
                return !newFile.ToString().Equals(existing.ToString(), StringComparison.Ordinal);
            }

            return true;
        }

        public static XDocument ReadExisting(string path, ILogger log)
        {
            XDocument result = null;

            if (File.Exists(path))
            {
                try
                {
                    using (var output = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        result = XDocument.Load(output);
                    }
                }
                catch (Exception ex)
                {
                    // Log a debug message and ignore, this will force an overwrite
                    log.LogDebug($"Failed to open imports file: {path} Error: {ex.Message}");
                }
            }

            return result;
        }

        public static List<MSBuildOutputFile> GetMSBuildOutputFiles(PackageSpec project,
            LockFile assetsFile,
            IEnumerable<RestoreTargetGraph> targetGraphs,
            IReadOnlyList<NuGetv3LocalRepository> repositories,
            RestoreRequest request,
            bool restoreSuccess,
            ILogger log,
            CancellationToken token)
        {
            // Generate file names
            var targetsPath = Path.Combine(request.RestoreOutputPath, $"{project.Name}.nuget.targets");
            var propsPath = Path.Combine(request.RestoreOutputPath, $"{project.Name}.nuget.props");

            if (request.RestoreOutputType == RestoreOutputType.NETCore)
            {
                var projFileName = Path.GetFileName(request.Project.RestoreMetadata.ProjectPath);

                targetsPath = Path.Combine(request.RestoreOutputPath, $"{projFileName}.nuget.g.targets");
                propsPath = Path.Combine(request.RestoreOutputPath, $"{projFileName}.nuget.g.props");
            }

            // Targets files contain a macro for the repository root. If only the user package folder was used
            // allow a replacement. If fallback folders were used the macro cannot be applied.
            // Do not use macros for fallback folders. Use only the first repository which is the user folder.
            var repositoryRoot = repositories.First().RepositoryRoot;

            // Invalid msbuild projects should write out an msbuild error target
            if (!targetGraphs.Any())
            {
                return GenerateMultiTargetFailureFiles(
                    targetsPath,
                    propsPath,
                    repositoryRoot,
                    restoreSuccess,
                    request.RestoreOutputType,
                    log,
                    token);
            }

            // Add additional conditionals for cross targeting
            var isCrossTargeting = request.Project.RestoreMetadata.CrossTargeting
                || request.Project.TargetFrameworks.Count > 1;

            Debug.Assert((!request.Project.RestoreMetadata.CrossTargeting && (request.Project.TargetFrameworks.Count < 2)
                || (request.Project.RestoreMetadata.CrossTargeting)),
                "Invalid crosstargeting and framework count combination");

            // ItemGroups for each file.
            var props = new List<MSBuildRestoreItemGroup>();
            var targets = new List<MSBuildRestoreItemGroup>();

            // Skip runtime graphs, msbuild targets may not come from RID specific packages.
            var ridlessTargets = assetsFile.Targets
                .Where(e => string.IsNullOrEmpty(e.RuntimeIdentifier));

            foreach (var ridlessTarget in ridlessTargets)
            {
                // There could be multiple string matches from the MSBuild project.
                var frameworkConditions = GetMatchingFrameworkStrings(project, ridlessTarget.TargetFramework)
                    .Select(match => string.Format(CultureInfo.InvariantCulture, TargetFrameworkCondition, match))
                    .ToArray();

                // Find matching target in the original target graphs.
                var targetGraph = targetGraphs.FirstOrDefault(e =>
                    string.IsNullOrEmpty(e.RuntimeIdentifier)
                    && ridlessTarget.TargetFramework == e.Framework);

                // Sort by dependency order, child package assets should appear higher in the 
                // msbuild targets and props files so that parents can depend on them.
                // Package -> PackageInfo
                // PackageInfo is kept lazy to avoid hitting the disk for packages
                // with no relevant assets.
                var sortedPackages = TopologicalSortUtility.SortPackagesByDependencyOrder(ConvertToPackageDependencyInfo(targetGraph.Flattened))
                                        .Select(sortedPkg =>
                                            new KeyValuePair<LockFileTargetLibrary, Lazy<LocalPackageSourceInfo>>(
                                                    key: ridlessTarget.Libraries.FirstOrDefault(assetsPkg =>
                                                        sortedPkg.Version == assetsPkg.Version
                                                        && sortedPkg.Id.Equals(assetsPkg.Name, StringComparison.OrdinalIgnoreCase)),
                                                    value: new Lazy<LocalPackageSourceInfo>(() =>
                                                        NuGetv3LocalRepositoryUtility.GetPackage(
                                                            repositories,
                                                            sortedPkg.Id,
                                                            sortedPkg.Version))))
                                        .ToArray();

                // build/ {packageId}.targets
                var buildTargetsGroup = new MSBuildRestoreItemGroup();
                buildTargetsGroup.Position = 2;

                buildTargetsGroup.Items.AddRange(sortedPackages.SelectMany(pkg =>
                    pkg.Key.Build.WithExtension(TargetsExtension)
                        .Select(e => pkg.Value.GetAbsolutePath(e)))
                        .Select(path => GetImportPath(path, repositoryRoot))
                        .Select(GenerateImport));

                targets.AddRange(GetGroupsWithConditions(buildTargetsGroup, isCrossTargeting, frameworkConditions));

                // props/ {packageId}.props
                var buildPropsGroup = new MSBuildRestoreItemGroup();
                buildPropsGroup.Position = 2;

                buildPropsGroup.Items.AddRange(sortedPackages.SelectMany(pkg =>
                    pkg.Key.Build.WithExtension(PropsExtension)
                        .Select(e => pkg.Value.GetAbsolutePath(e)))
                        .Select(path => GetImportPath(path, repositoryRoot))
                        .Select(GenerateImport));

                props.AddRange(GetGroupsWithConditions(buildPropsGroup, isCrossTargeting, frameworkConditions));

                if (isCrossTargeting)
                {
                    // buildCrossTargeting/ {packageId}.targets
                    var buildCrossTargetsGroup = new MSBuildRestoreItemGroup();
                    buildCrossTargetsGroup.Position = 0;

                    buildCrossTargetsGroup.Items.AddRange(sortedPackages.SelectMany(pkg =>
                        pkg.Key.BuildCrossTargeting.WithExtension(TargetsExtension)
                            .Select(e => pkg.Value.GetAbsolutePath(e)))
                            .Select(path => GetImportPath(path, repositoryRoot))
                            .Select(GenerateImport));

                    targets.Add(buildCrossTargetsGroup);

                    // buildCrossTargeting/ {packageId}.props
                    var buildCrossPropsGroup = new MSBuildRestoreItemGroup();
                    buildCrossPropsGroup.Position = 0;

                    buildCrossPropsGroup.Items.AddRange(sortedPackages.SelectMany(pkg =>
                        pkg.Key.BuildCrossTargeting.WithExtension(PropsExtension)
                            .Select(e => pkg.Value.GetAbsolutePath(e)))
                            .Select(path => GetImportPath(path, repositoryRoot))
                            .Select(GenerateImport));

                    props.Add(buildCrossPropsGroup);
                }

                // ContentFiles are read by the build task, not by NuGet
                // for UAP with project.json.
                if (request.RestoreOutputType != RestoreOutputType.UAP)
                {
                    // contentFiles/
                    var allLanguages = new SortedSet<string>(
                            sortedPackages.SelectMany(pkg =>
                                pkg.Key.ContentFiles.Select(item => item.CodeLanguage)),
                            StringComparer.OrdinalIgnoreCase);

                    // Create a group for every package, with the nearest from each of allLanguages
                    props.AddRange(sortedPackages.SelectMany(pkg =>
                         pkg.Key.ContentFiles
                                .OrderBy(e => e.Path, StringComparer.Ordinal)
                                .Select(e =>
                                    new KeyValuePair<LockFileContentFile, string>(
                                        key: e,
                                        value: pkg.Value.GetAbsolutePath(GetImportPath(e.Path, repositoryRoot))))
                                .GetLanguageGroups(allLanguages))
                        .GroupBy(e => e.Key, e => e.Value)
                        .Select(group => MSBuildRestoreItemGroup.Create(
                            items: group.SelectMany(e => e)
                                        .Where(e => PackagingCoreConstants.EmptyFolder != e.Key.Path)
                                        .Select(e => GenerateContentFilesItem(e.Value, e.Key)),
                            position: 1,
                            conditions: GetLanguageConditions(group.Key)))
                        .Where(group => group.Items.Count > 0)
                        .SelectMany(group => GetGroupsWithConditions(group, isCrossTargeting, frameworkConditions)));
                }
            }

            // Add exclude all condition to all groups
            foreach (var group in props.Concat(targets))
            {
                group.Conditions.Add(ExcludeAllCondition);
            }

            // Create XML, these may be null if the file should be deleted/not written out.
            var propsXML = GenerateMSBuildFile(props, repositoryRoot, request.RestoreOutputType, restoreSuccess);
            var targetsXML = GenerateMSBuildFile(targets, repositoryRoot, request.RestoreOutputType, restoreSuccess);

            // Return all files to write out or delete.
            return new List<MSBuildOutputFile>
            {
                new MSBuildOutputFile(propsPath, propsXML),
                new MSBuildOutputFile(targetsPath, targetsXML)
            };
        }

        private static IEnumerable<string> GetLanguageConditions(string language)
        {
            if (!PackagingConstants.AnyCodeLanguage.Equals(language, StringComparison.OrdinalIgnoreCase))
            {
                yield return string.Format(CultureInfo.InvariantCulture, LanguageCondition, language);
            }
        }

        private static Dictionary<string, List<KeyValuePair<LockFileContentFile, string>>> GetLanguageGroups(
            this IEnumerable<KeyValuePair<LockFileContentFile, string>> items,
            SortedSet<string> allLanguages)
        {
            // Fallback group
            var anyGroup = items.Where(e =>
                PackagingConstants.AnyCodeLanguage.Equals(e.Key.CodeLanguage, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var groups = new Dictionary<string, List<KeyValuePair<LockFileContentFile, string>>>(StringComparer.OrdinalIgnoreCase);

            foreach (var lang in allLanguages)
            {
                var langItems = new List<KeyValuePair<LockFileContentFile, string>>();
                groups.Add(lang, langItems);

                langItems.AddRange(items.Where(e =>
                    lang.Equals(e.Key.CodeLanguage, StringComparison.OrdinalIgnoreCase)));

                if (langItems.Count < 1)
                {
                    langItems.AddRange(anyGroup);
                }
            }

            return groups;
        }

        private static IEnumerable<MSBuildRestoreItemGroup> GetGroupsWithConditions(
            MSBuildRestoreItemGroup original,
            bool isCrossTargeting,
            params string[] conditions)
        {
            if (!isCrossTargeting)
            {
                // No changes needed
                yield return original;
            }

            foreach (var condition in conditions)
            {
                yield return new MSBuildRestoreItemGroup()
                {
                    Position = original.Position,
                    Items = original.Items,
                    Conditions = original.Conditions.Concat(new[] { condition }).ToList()
                };
            }
        }

        private static string GetAbsolutePath(this Lazy<LocalPackageSourceInfo> package, LockFileItem item)
        {
            return Path.Combine(package.Value.Package.ExpandedPath, LockFileUtils.ToDirectorySeparator(item.Path));
        }

        private static IEnumerable<LockFileItem> WithExtension(this IList<LockFileItem> items, string extension)
        {
            if (items == null)
            {
                return Enumerable.Empty<LockFileItem>();
            }

            return items.Where(c => extension.Equals(Path.GetExtension(c.Path), StringComparison.OrdinalIgnoreCase));
        }

        private static HashSet<string> GetMatchingFrameworkStrings(PackageSpec spec, NuGetFramework framework)
        {
            // Ignore case since msbuild does
            var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            matches.UnionWith(spec.RestoreMetadata.OriginalTargetFrameworks
                .Where(s => framework.Equals(NuGetFramework.Parse(s))));

            // If there were no matches, use the generated name
            if (matches.Count < 1)
            {
                matches.Add(framework.GetShortFolderName());
            }

            return matches;
        }

        private static HashSet<PackageDependencyInfo> ConvertToPackageDependencyInfo(
            ISet<GraphItem<RemoteResolveResult>> items)
        {
            var result = new HashSet<PackageDependencyInfo>(PackageIdentity.Comparer);

            foreach (var item in items)
            {
                var dependencies =
                    item.Data?.Dependencies?.Select(
                        dependency => new PackageDependency(dependency.Name, VersionRange.All));

                result.Add(new PackageDependencyInfo(item.Key.Name, item.Key.Version, dependencies));
            }

            return result;
        }
    }
}