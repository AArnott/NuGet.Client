﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using NuGet.Commands;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectManagement;
using NuGet.ProjectManagement.Projects;
using NuGet.ProjectModel;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Task = System.Threading.Tasks.Task;

namespace NuGet.PackageManagement.VisualStudio
{
/// <summary>
/// An implementation of <see cref="NuGetProject"/> that interfaces with VS project APIs to coordinate
/// packages in a legacy CSProj with package references.
/// </summary>
    public class LegacyCSProjPackageReferenceProject : BuildIntegratedNuGetProject
    {
        private const string _includeAssets = "IncludeAssets";
        private const string _excludeAssets = "ExcludeAssets";
        private const string _privateAssets = "PrivateAssets";

        private static Array _desiredPackageReferenceMetadata;

        private readonly IEnvDTEProjectAdapter _project;

        private IScriptExecutor _scriptExecutor;
        private string _projectName;
        private string _projectUniqueName;
        private string _projectFullPath;

        static LegacyCSProjPackageReferenceProject()
        {
            _desiredPackageReferenceMetadata = Array.CreateInstance(typeof(string), 3);
            _desiredPackageReferenceMetadata.SetValue(_includeAssets, 0);
            _desiredPackageReferenceMetadata.SetValue(_excludeAssets, 1);
            _desiredPackageReferenceMetadata.SetValue(_privateAssets, 2);
        }

        public LegacyCSProjPackageReferenceProject(
            IEnvDTEProjectAdapter project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            _project = project;
            _projectName = _project.Name;
            _projectUniqueName = _project.UniqueName;
            _projectFullPath = _project.ProjectFullPath;


            InternalMetadata.Add(NuGetProjectMetadataKeys.Name, _projectName);
            InternalMetadata.Add(NuGetProjectMetadataKeys.UniqueName, _projectUniqueName);
            InternalMetadata.Add(NuGetProjectMetadataKeys.FullPath, _projectFullPath);
        }

        public override string ProjectName => _projectName;

        private IScriptExecutor ScriptExecutor
        {
            get
            {
                if (_scriptExecutor == null)
                {
                    _scriptExecutor = ServiceLocator.GetInstanceSafe<IScriptExecutor>();
                }

                return _scriptExecutor;
            }
        }

        public override async Task<string> GetAssetsFilePathAsync()
        {
            return Path.Combine(await GetBaseIntermediatePathAsync(), LockFileFormat.AssetsFileName); ;
        }

        public override async Task<bool> ExecuteInitScriptAsync(
            PackageIdentity identity,
            string packageInstallPath,
            INuGetProjectContext projectContext,
            bool throwOnFailure)
        {
            return
                await
                    ScriptExecutorUtil.ExecuteScriptAsync(identity, packageInstallPath, projectContext, ScriptExecutor,
                        _project.DTEProject, throwOnFailure);
        }

        #region IDependencyGraphProject

        public override string MSBuildProjectPath => _projectFullPath;

        public override async Task<IReadOnlyList<PackageSpec>> GetPackageSpecsAsync(DependencyGraphCacheContext context)
        {
            PackageSpec packageSpec;
            if (context.PackageSpecCache.TryGetValue(_projectFullPath, out packageSpec))
            {
                return new[] { packageSpec };
            }

            packageSpec = await GetPackageSpecAsync();
            context.PackageSpecCache.Add(_projectFullPath, packageSpec);
            return new[] { packageSpec };
        }

        #endregion

        #region NuGetProject

        public override async Task<IEnumerable<PackageReference>> GetInstalledPackagesAsync(CancellationToken token)
        {
            return GetPackageReferences(await GetPackageSpecAsync());
        }

        public override async Task<bool> InstallPackageAsync(PackageIdentity packageIdentity, DownloadResourceResult downloadResourceResult, INuGetProjectContext nuGetProjectContext, CancellationToken token)
        {
            var success = false;
            await ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // We don't adjust package reference metadata from UI
                _project.AddOrUpdateLegacyCSProjPackage(
                    packageIdentity.Id,
                    packageIdentity.Version.ToNormalizedString(),
                    new string[] { },
                    new string[] { });

                success = true;
            });

            return success;
        }

        public override async Task<Boolean> UninstallPackageAsync(PackageIdentity packageIdentity, INuGetProjectContext nuGetProjectContext, CancellationToken token)
        {
            var success = false;
            await ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                _project.RemoveLegacyCSProjPackage(packageIdentity.Id);

                success = true;
            });

            return success;
        }

        #endregion

        private async Task<string> GetBaseIntermediatePathAsync()
        {
            string baseIntermediatePath = String.Empty;

            await ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                baseIntermediatePath = GetBaseIntermediatePath();
            });

            return baseIntermediatePath;
        }

        private string GetBaseIntermediatePath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var baseIntermediatePath = _project.BaseIntermediatePath;

            if (string.IsNullOrEmpty(baseIntermediatePath) || !Directory.Exists(baseIntermediatePath))
            {
                throw new InvalidDataException(nameof(_project.BaseIntermediatePath));
            }

            return baseIntermediatePath;
        }

        private static string[] GetProjectReferences(PackageSpec packageSpec)
        {
            // There is only one target framework for legacy csproj projects
            var targetFramework = packageSpec.TargetFrameworks.FirstOrDefault();
            if (targetFramework == null)
            {
                return new string[] { };
            }

            return targetFramework.Dependencies
                .Where(d => d.LibraryRange.TypeConstraint == LibraryDependencyTarget.ExternalProject)
                .Select(d => d.LibraryRange.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static PackageReference[] GetPackageReferences(PackageSpec packageSpec)
        {
            var frameworkSorter = new NuGetFrameworkSorter();

            return packageSpec
                .TargetFrameworks
                .SelectMany(f => GetPackageReferences(f.Dependencies, f.FrameworkName))
                .GroupBy(p => p.PackageIdentity)
                .Select(g => g.OrderBy(p => p.TargetFramework, frameworkSorter).First())
                .ToArray();
        }

        private static IEnumerable<PackageReference> GetPackageReferences(IEnumerable<LibraryDependency> libraries, NuGetFramework targetFramework)
        {
            return libraries
                .Where(l => l.LibraryRange.TypeConstraint == LibraryDependencyTarget.Package)
                .Select(l => ToPackageReference(l, targetFramework));
        }

        private static PackageReference ToPackageReference(LibraryDependency library, NuGetFramework targetFramework)
        {
            var identity = new PackageIdentity(
                library.LibraryRange.Name,
                library.LibraryRange.VersionRange.MinVersion);

            return new PackageReference(identity, targetFramework);
        }

        private async Task<PackageSpec> GetPackageSpecAsync()
        {
            PackageSpec packageSpec = null;
            await ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                packageSpec = GetPackageSpec();
            });

            return packageSpec;
        }

        private PackageSpec GetPackageSpec()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var projectReferences = _project.GetLegacyCSProjProjectReferences(_desiredPackageReferenceMetadata)
                .Select(ToProjectRestoreReference);

            var packageReferences = _project.GetLegacyCSProjPackageReferences(_desiredPackageReferenceMetadata)
                .Select(ToPackageLibraryDependency);

            var projectTfi = new TargetFrameworkInformation()
            {
                FrameworkName = _project.TargetNuGetFramework,
                Dependencies = packageReferences.ToList()
            };

            // In legacy CSProj, we only have one target framework per project
            var tfis = new TargetFrameworkInformation[] { projectTfi };
            return new PackageSpec(tfis)
            {
                Name = _projectName ?? _projectUniqueName,
                FilePath = _projectFullPath,
                RestoreMetadata = new ProjectRestoreMetadata
                {
                    OutputType = RestoreOutputType.NETCore,
                    OutputPath = GetBaseIntermediatePath(),
                    ProjectPath = _projectFullPath,
                    ProjectName = _projectName ?? _projectUniqueName,
                    ProjectUniqueName = _projectFullPath,
                    OriginalTargetFrameworks = tfis
                        .Select(tfi => tfi.FrameworkName.GetShortFolderName())
                        .ToList(),
                    TargetFrameworks = new List<ProjectRestoreMetadataFrameworkInfo>()
                    {
                        new ProjectRestoreMetadataFrameworkInfo(tfis[0].FrameworkName)
                        {
                            ProjectReferences = projectReferences.ToList()
                        }
                    }
                }
            };
        }

        private static ProjectRestoreReference ToProjectRestoreReference(LegacyCSProjProjectReference item)
        {
            var reference = new ProjectRestoreReference()
            {
                ProjectUniqueName = item.UniqueName,
                ProjectPath = item.UniqueName
            };

            MSBuildRestoreUtility.ApplyIncludeFlags(
                reference,
                GetProjectMetadataValue(item, _includeAssets),
                GetProjectMetadataValue(item, _excludeAssets),
                GetProjectMetadataValue(item, _privateAssets));

            return reference;
        }

        private static LibraryDependency ToPackageLibraryDependency(LegacyCSProjPackageReference item)
        {
            var dependency = new LibraryDependency
            {
                LibraryRange = new LibraryRange(
                    name: item.Name,
                    versionRange: new VersionRange(new NuGetVersion(item.Version)),
                    typeConstraint: LibraryDependencyTarget.Package)
            };

            MSBuildRestoreUtility.ApplyIncludeFlags(
                dependency,
                GetPackageMetadataValue(item, _includeAssets),
                GetPackageMetadataValue(item, _excludeAssets),
                GetPackageMetadataValue(item, _privateAssets));

            return dependency;
        }

        private static string GetProjectMetadataValue(LegacyCSProjProjectReference item, string metadataElement)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (string.IsNullOrEmpty(metadataElement))
            {
                throw new ArgumentNullException(nameof(metadataElement));
            }

            if (item.MetadataElements == null || item.MetadataValues == null)
            {
                return String.Empty; // no metadata for project
            }

            var index = Array.IndexOf(item.MetadataElements, metadataElement);
            if (index >= 0)
            {
                return item.MetadataValues.GetValue(index) as string;
            }

            return string.Empty;
        }

        private static string GetPackageMetadataValue(LegacyCSProjPackageReference item, string metadataElement)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (string.IsNullOrEmpty(metadataElement))
            {
                throw new ArgumentNullException(nameof(metadataElement));
            }

            if (item.MetadataElements == null || item.MetadataValues == null)
            {
                return String.Empty; // no metadata for package
            }

            var index = Array.IndexOf(item.MetadataElements, metadataElement);
            if (index >= 0)
            {
                return item.MetadataValues.GetValue(index) as string;
            }

            return string.Empty;
        }
    }
}