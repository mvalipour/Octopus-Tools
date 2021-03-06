﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NuGet;
using Octopus.Client.Model;
using OctopusTools.Diagnostics;
using OctopusTools.Infrastructure;
using log4net;

namespace OctopusTools.Commands
{
    [Command("create-release", Description = "Creates (and, optionally, deploys) a release.")]
    public class CreateReleaseCommand : DeploymentCommandBase
    {
        readonly IPackageVersionResolver versionResolver;

        public CreateReleaseCommand(IOctopusRepositoryFactory repositoryFactory, ILog log, IPackageVersionResolver versionResolver)
            : base(repositoryFactory, log)
        {
            this.versionResolver = versionResolver;

            DeployToEnvironmentNames = new List<string>();
            DeploymentStatusCheckSleepCycle = TimeSpan.FromSeconds(10);
            DeploymentTimeout = TimeSpan.FromMinutes(10);
        }

        public string ProjectName { get; set; }
        public List<string> DeployToEnvironmentNames { get; set; }
        public string VersionNumber { get; set; }
        public string ReleaseNotes { get; set; }
        public bool IgnoreIfAlreadyExists { get; set; }

        protected override void SetOptions(OptionSet options)
        {
            SetCommonOptions(options);
            options.Add("project=", "Name of the project", v => ProjectName = v);
            options.Add("deployto=", "[Optional] Environment to automatically deploy to, e.g., Production", v => DeployToEnvironmentNames.Add(v));
            options.Add("releaseNumber=|version=", "[Optional] Release number to use for the new release.", v => VersionNumber = v);
            options.Add("defaultpackageversion=|packageversion=", "Default version number of all packages to use for this release.", v => versionResolver.Default(v));
            options.Add("package=", "[Optional] Version number to use for a package in the release. Format: --package={StepName}:{Version}", v => versionResolver.Add(v));
            options.Add("packagesFolder=", "[Optional] A folder containing NuGet packages from which we should get versions.", v => versionResolver.AddFolder(v));
            options.Add("releasenotes=", "[Optional] Release Notes for the new release.", v => ReleaseNotes = v);
            options.Add("releasenotesfile=", "[Optional] Path to a file that contains Release Notes for the new release.", ReadReleaseNotesFromFile);
            options.Add("ignoreexisting", "", v => IgnoreIfAlreadyExists = true);
        }

        protected override void Execute()
        {
            if (string.IsNullOrWhiteSpace(ProjectName)) throw new CommandException("Please specify a project name using the parameter: --project=XYZ");

            Log.Debug("Finding project: " + ProjectName);
            var project = Repository.Projects.FindByName(ProjectName);
            if (project == null)
                throw new CommandException("Could not find a project named: " + ProjectName);

            Log.Debug("Finding deployment process for project: " + ProjectName);
            var deploymentProcess = Repository.DeploymentProcesses.Get(project.DeploymentProcessId);

            Log.Debug("Finding release template...");
            var releaseTemplate = Repository.DeploymentProcesses.GetTemplate(deploymentProcess);

            var plan = new ReleasePlan(releaseTemplate, versionResolver);

            if (plan.UnresolvedSteps.Count > 0)
            {
                Log.Debug("Resolving NuGet package versions...");
                foreach (var unresolved in plan.UnresolvedSteps)
                {
                    if (!unresolved.IsResolveable)
                    {
                        Log.ErrorFormat("The version number for step '{0}' cannot be automatically resolved because the feed or package ID is dynamic.", unresolved.StepName);
                        continue;
                    }

                    Log.Debug("Finding latest NuGet package for step: " + unresolved.StepName);

                    var feed = Repository.Feeds.Get(unresolved.NuGetFeedId);
                    if (feed == null)
                        throw new CommandException(string.Format("Could not find a feed with ID {0}, which is used by step: " + unresolved.StepName, unresolved.NuGetFeedId));

                    var packages = Repository.Client.Get<List<PackageResource>>(feed.Link("VersionsTemplate"), new {packageIds = new[] {unresolved.PackageId}});
                    var version = packages.FirstOrDefault();
                    if (version == null)
                    {
                        Log.ErrorFormat("Could not find any packages with ID '{0}' in the feed '{1}'", unresolved.PackageId, feed.FeedUri);
                    }
                    else
                    {
                        unresolved.SetVersionFromLatest(version.Version);
                    }
                }
            }

            var versionNumber = VersionNumber;
            if (string.IsNullOrWhiteSpace(versionNumber))
            {
                Log.Warn("A --version parameter was not specified, so a version number was automatically selected based on the highest package version.");
                versionNumber = plan.GetHighestVersionNumber();
            }

            if (plan.Steps.Count > 0)
            {
                Log.Info("Release plan for release:    " + versionNumber);
                Log.Info("Steps: ");
                Log.Info(plan.FormatAsTable());
            }

            if (plan.HasUnresolvedSteps())
            {
                throw new CommandException("Package versions could not be resolved for one or more of the package steps in this release. See the errors above for details. Either ensure the latest version of the package can be automatically resolved, or set the version to use specifically by using the --package argument.");
            }

            Log.Debug("Creating release...");

            if (IgnoreIfAlreadyExists)
            {
                var found = Repository.Releases.FindOne(r => r.Version == versionNumber);
                if (found != null)
                {
                    Log.Info("A release with the number " + versionNumber + " already exists.");
                    return;
                }                
            }

            var release = Repository.Releases.Create(new ReleaseResource(versionNumber, project.Id)
            {
                ReleaseNotes = ReleaseNotes,
                SelectedPackages = plan.GetSelections()
            });
            Log.Info("Release " + release.Version + " created successfully!");

            Log.ServiceMessage("setParameter", new { name = "octo.releaseNumber", value = release.Version });

            DeployRelease(project, release, DeployToEnvironmentNames);
        }

        private void ReadReleaseNotesFromFile(string value)
        {
            try
            {
                ReleaseNotes = File.ReadAllText(value);
            }
            catch (IOException ex)
            {
                throw new CommandException(ex.Message);
            }
        }
    }
}
