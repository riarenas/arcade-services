// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Maestro.Contracts;
using Maestro.Data;
using Maestro.Data.Models;
using Microsoft.DotNet.DarcLib;
using Microsoft.DotNet.ServiceFabric.ServiceHost;
using Microsoft.DotNet.ServiceFabric.ServiceHost.Actors;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Runtime;
using Microsoft.ServiceFabric.Data;
using Asset = Maestro.Contracts.Asset;
using AssetData = Microsoft.DotNet.Maestro.Client.Models.AssetData;

namespace SubscriptionActorService
{
    public abstract partial class MergeActorImplementation : IMergeActor, IActionTracker
    {
        // This property stores the id of the official build that gets triggered before the merge
        public const string PreMergeOfficialBuildKey = "PreMergeOfficialBuild";

        // This property stores a combination of (account, project, build definition, SHA from the merge)
        // in order to search for an official build that gets triggered after the merge.
        public const string PostMergeOfficialBuildKey = "PostMergeOfficialBuild";

        // This property stores the dependency updates that need to be added to the target branch
        public const string UpdatesKey = "Updates";

        public const string OfficialBuildRunning = "OfficialBuildRunning";
        public const string ChangesMerged = "ChangesMerged";

        private const int maxAttemptsForCancelling = 5;

        protected MergeActorImplementation(
            ActorId id,
            IReminderManager reminders,
            IActorStateManager stateManager,
            BuildAssetRegistryContext context,
            IRemoteFactory darcFactory,
            ILoggerFactory loggerFactory,
            IActionRunner actionRunner,
            IActorProxyFactory<ISubscriptionActor> subscriptionActorFactory)
        {
            Id = id;
            Reminders = reminders;
            StateManager = stateManager;
            Context = context;
            DarcRemoteFactory = darcFactory;
            ActionRunner = actionRunner;
            SubscriptionActorFactory = subscriptionActorFactory;
            Logger = loggerFactory.CreateLogger(GetType());
        }

        public ILogger Logger { get; }
        public ActorId Id { get; }
        public IReminderManager Reminders { get; }
        public IActorStateManager StateManager { get; }
        public BuildAssetRegistryContext Context { get; }
        public IRemoteFactory DarcRemoteFactory { get; }
        public IActionRunner ActionRunner { get; }
        public IActorProxyFactory<ISubscriptionActor> SubscriptionActorFactory { get; }

        public async Task TrackSuccessfulAction(string action, string result)
        {
            (string repo, string branch) = await GetTargetAsync();
            RepositoryBranchUpdate update = await DependencyUpdateActorUtils.GetRepositoryBranchUpdate(Context, repo, branch);

            update.Action = action;
            update.ErrorMessage = result;
            update.Method = null;
            update.Arguments = null;
            update.Success = true;
            await Context.SaveChangesAsync();
        }

        public async Task TrackFailedAction(string action, string result, string method, string arguments)
        {
            (string repo, string branch) = await GetTargetAsync();
            RepositoryBranchUpdate update = await DependencyUpdateActorUtils.GetRepositoryBranchUpdate(Context, repo, branch);

            update.Action = action;
            update.ErrorMessage = result;
            update.Method = method;
            update.Arguments = arguments;
            update.Success = false;
            await Context.SaveChangesAsync();
        }

        public Task<string> RunActionAsync(string method, string arguments)
        {
            return ActionRunner.RunAction(this, method, arguments);
        }

        Task IMergeActor.UpdateAssetsAsync(Guid subscriptionId, int buildId, string sourceRepo, string sourceSha, List<Asset> assets)
        {
            return ActionRunner.ExecuteAction(() => UpdateAssetsAsync(subscriptionId, buildId, sourceRepo, sourceSha, assets));
        }

        protected abstract Task<(string repository, string branch)> GetTargetAsync();

        /// <summary>
        /// Retrieve the build from a database build id.
        /// </summary>
        /// <param name="buildId">Build id</param>
        /// <returns>Build</returns>
        private Task<Build> GetBuildAsync(int buildId)
        {
            return Context.Builds.FindAsync(buildId).AsTask();
        }

        public Task<bool?> RunWaitForOfficialBuild()
        {
            return ActionRunner.ExecuteAction(() => WaitForOfficialBuild());
        }

        public async Task<ActionResult<bool>> CancelOfficialBuild()
        {
            ConditionalValue<PostMergeOfficialBuildParameters> maybeOfficialBuild =
                await StateManager.TryGetStateAsync<PostMergeOfficialBuildParameters>(PostMergeOfficialBuildKey);

            string message = "No parameters for official build were found";
            bool result = false;
            if (maybeOfficialBuild.HasValue)
            {
                result = true;
                PostMergeOfficialBuildParameters officialBuildToLookFor = maybeOfficialBuild.Value;
                IAzureDevOpsClient azdoClient = await DarcRemoteFactory.GetAzdoClientForAccount(officialBuildToLookFor.Account, Logger);

                if (officialBuildToLookFor.Attempt <= maxAttemptsForCancelling)
                {
                    AzureDevOpsBuild build = await azdoClient.FindBuildForSHA(
                    officialBuildToLookFor.Account,
                    officialBuildToLookFor.Project,
                    officialBuildToLookFor.Definition,
                    officialBuildToLookFor.SHA);
                    if (build != null)
                    {
                        await azdoClient.CancelAzureDevOpsBuildAsync(
                            officialBuildToLookFor.Account,
                            officialBuildToLookFor.Project,
                            build.Id);
                        message = $"successfully cancelled official build for SHA {officialBuildToLookFor.SHA}. Hopefully the world doesn't explode";
                    }
                    else
                    {
                        message = $"Could not find a matching official build, {maxAttemptsForCancelling - officialBuildToLookFor.Attempt} remaining.";
                        officialBuildToLookFor.Attempt++;
                        await StateManager.SetStateAsync(
                            PostMergeOfficialBuildKey,
                            officialBuildToLookFor);
                        await StateManager.SaveStateAsync();
                    }
                }
                else
                {
                    message = $"Could not find a matching official build after {maxAttemptsForCancelling} attempts, just leave things alone";
                }
            }

            return ActionResult.Create(result, message);
        }

        public async Task<ActionResult<bool?>> WaitForOfficialBuild()
        {
            ConditionalValue<PreMergeOfficialBuildParameters> maybeOfficialBuild 
                = await StateManager.TryGetStateAsync<PreMergeOfficialBuildParameters>(PreMergeOfficialBuildKey);
            bool? result = null;
            string message = "Official build has not completed";

            if (maybeOfficialBuild.HasValue)
            {
                PreMergeOfficialBuildParameters runningOfficialBuild = maybeOfficialBuild.Value;

                IAzureDevOpsClient azdoClient = await DarcRemoteFactory.GetAzdoClientForAccount(runningOfficialBuild.Account, Logger);
                AzureDevOpsBuild build = await azdoClient.GetBuildAsync(
                    runningOfficialBuild.Account,
                    runningOfficialBuild.Project,
                    runningOfficialBuild.BuildId);

                if (build.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
                {
                    if (build.Result.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
                    {
                        result = true;
                        message = "official build succeeded";
                        await MergeChangesToTargetBranchAsync(runningOfficialBuild);
                    }
                    else
                    {
                        result = false;
                        message = "official build failed";
                    }
                }
            }
            return ActionResult.Create(result, message);
        }

        private async Task MergeChangesToTargetBranchAsync(PreMergeOfficialBuildParameters runningOfficialBuild)
        {
            (string targetRepository, string targetBranch) = await GetTargetAsync();
            IRemote darcRemote = await DarcRemoteFactory.GetRemoteAsync(targetRepository, Logger);
            string mergeSHA = await darcRemote.TryFastForwardMergeBranchesAsync(targetRepository, runningOfficialBuild.UpdatesBranch, targetBranch);

            if (!string.IsNullOrEmpty(mergeSHA))
            {
                Logger.LogInformation($"Succeeded merging branch {runningOfficialBuild.UpdatesBranch} into {targetBranch} in repo {targetRepository}.");
                await UpdateSubscriptionsForMergedUpdateAsync(new List<PreMergeOfficialBuildParameters> { runningOfficialBuild });

                var postMergeParameters = new PostMergeOfficialBuildParameters()
                {
                    Account = runningOfficialBuild.Account,
                    Project = runningOfficialBuild.Project,
                    Definition = runningOfficialBuild.Definition,
                    SHA = mergeSHA,
                    Attempt = 0,
                };

                await StateManager.SetStateAsync(PostMergeOfficialBuildKey, postMergeParameters);
                await StateManager.TryRemoveStateAsync(PreMergeOfficialBuildKey);
                await Reminders.TryRegisterReminderAsync(
                    ChangesMerged,
                    null,
                    TimeSpan.FromMinutes(2),
                    TimeSpan.FromMinutes(2));
            }
        }

        private async Task UpdateSubscriptionsForMergedUpdateAsync(
            IEnumerable<PreMergeOfficialBuildParameters> preMergeUpdates)
        {
            foreach (PreMergeOfficialBuildParameters update in preMergeUpdates)
            {
                ISubscriptionActor actor = SubscriptionActorFactory.Lookup(new ActorId(update.SubscriptionId));
                if (!await actor.UpdateForMergedDependencyUpdate((int)update.BuildId))
                {
                    Logger.LogInformation($"Failed to update subscription {update.SubscriptionId} for merged commit.");
                    await Reminders.TryUnregisterReminderAsync(ChangesMerged);
                    await Reminders.TryUnregisterReminderAsync(OfficialBuildRunning);
                    await StateManager.TryRemoveStateAsync(PreMergeOfficialBuildKey);
                    await StateManager.TryRemoveStateAsync(PostMergeOfficialBuildKey);
                }
            }
        }

        private async Task AddDependencyFlowEventsAsync(
            List<(UpdateAssetsParameters update, List<DependencyUpdate> deps)> subscriptionPullRequestUpdates,
            DependencyFlowEventType flowEvent,
            DependencyFlowEventReason reason,
            MergePolicyCheckResult policy,
            string repoUrl)
        {
            foreach ((UpdateAssetsParameters update, _) in subscriptionPullRequestUpdates)
            {
                ISubscriptionActor actor = SubscriptionActorFactory.Lookup(new ActorId(update.SubscriptionId));
                if (!await actor.AddDependencyFlowEventAsync(update.BuildId, flowEvent, reason, policy, "Merge", repoUrl))
                {
                    Logger.LogInformation($"Failed to add dependency flow event for {update.SubscriptionId}.");
                }
            }
        }

        /// <summary>
        ///     Applies asset updates for the target repository and branch from the given build and list of assets.
        /// </summary>
        /// <param name="subscriptionId">The id of the subscription the update comes from</param>
        /// <param name="buildId">The build that the updated assets came from</param>
        /// <param name="sourceSha">The commit hash that built the assets</param>
        /// <param name="assets">The list of assets</param>
        /// <returns></returns>
        [ActionMethod("Updating assets for subscription: {subscriptionId}, build: {buildId}")]
        public async Task<ActionResult<object>> UpdateAssetsAsync(
            Guid subscriptionId,
            int buildId,
            string sourceRepo,
            string sourceSha,
            List<Asset> assets)
        {
            var updateParameter = new UpdateAssetsParameters
            {
                SubscriptionId = subscriptionId,
                BuildId = buildId,
                SourceSha = sourceSha,
                SourceRepo = sourceRepo,
                Assets = assets,
                IsCoherencyUpdate = false
            };


            int officialBuildId = await QueueOfficialBuildWithChangesAsync(new List<UpdateAssetsParameters> { updateParameter });
            if (officialBuildId == -1)
            {
                return ActionResult.Create<object>(null, "Updates require no changes, no build was queued.");
            }

            return ActionResult.Create<object>(null, $"build with Id {officialBuildId} was queued");

        }

        /// <summary>
        ///     Queues an official build of the target repository with the changes applied.
        /// </summary>
        /// <param name="updates"></param>
        /// <returns>The pull request url when a pr was created; <see langref="null" /> if no PR is necessary</returns>
        private async Task<int> QueueOfficialBuildWithChangesAsync(List<UpdateAssetsParameters> updates)
        {
            (string targetRepository, string targetBranch) = await GetTargetAsync();
            IRemote darcRemote = await DarcRemoteFactory.GetRemoteAsync(targetRepository, Logger);

            List<(UpdateAssetsParameters update, List<DependencyUpdate> deps)> requiredUpdates =
                await GetRequiredUpdates(updates, DarcRemoteFactory, targetRepository, targetBranch);

            (string account, string project, int definitionId) =
                InferOfficialBuildDefinitionForRepo(targetRepository, targetBranch);

            if (requiredUpdates.Count < 1 || account == string.Empty)
            {
                // If we can't queue the official build, then mark the dependency flow as completed as nothing to do.
                await AddDependencyFlowEventsAsync(
                        requiredUpdates,
                        DependencyFlowEventType.Completed,
                        DependencyFlowEventReason.NothingToDo,
                        MergePolicyCheckResult.PendingPolicies,
                        targetRepository);
                return -1;
            }

            string newBranchName = $"darc-{targetBranch}-{Guid.NewGuid()}";
            await darcRemote.CreateNewBranchAsync(targetRepository, targetBranch, newBranchName);

            try
            {
                await CommitUpdatesAsync(requiredUpdates, DarcRemoteFactory, targetRepository, newBranchName);

                string sha = await darcRemote.GetLatestCommitAsync(targetRepository, newBranchName);

                IAzureDevOpsClient azdoClient = await DarcRemoteFactory.GetAzdoClientForAccount(account, Logger);

                int buildId = await azdoClient.StartNewBuildAsync(account, project, definitionId, newBranchName, sha);

                var preMergeOfficialBuildData = new PreMergeOfficialBuildParameters
                {
                    SubscriptionId = updates.First().SubscriptionId,
                    Account = account,
                    Project = project,
                    UpdatesBranch = newBranchName,
                    Definition = definitionId,
                    BuildId = buildId
                };

                await StateManager.SetStateAsync(PreMergeOfficialBuildKey, preMergeOfficialBuildData);
                await StateManager.SaveStateAsync();
                await Reminders.TryRegisterReminderAsync(
                    OfficialBuildRunning,
                    null,
                    TimeSpan.FromMinutes(5),
                    TimeSpan.FromMinutes(5));
                return buildId;
            }
            catch
            {
                await darcRemote.DeleteBranchAsync(targetRepository, newBranchName);
                throw;
            }
        }

        // TODO: This is another good cadidate for extracting to a base class for both actors, but this one doesn't need a description
        /// <summary>
        /// Commit a dependency update to a target branch 
        /// </summary>
        /// <param name="requiredUpdates">Version updates to apply</param>
        /// </param>
        /// <param name="remoteFactory">Remote factory for generating remotes based on repo uri</param>
        /// <param name="targetRepository">Target repository that the updates should be applied to</param>
        /// <param name="newBranchName">Target branch the updates should be to</param>
        /// <returns></returns>
        private async Task CommitUpdatesAsync(
            List<(UpdateAssetsParameters update, List<DependencyUpdate> deps)> requiredUpdates,
            IRemoteFactory remoteFactory,
            string targetRepository,
            string newBranchName)
        {
            // First run through non-coherency and then do a coherency
            // message if one exists.
            List<(UpdateAssetsParameters update, List<DependencyUpdate> deps)> nonCoherencyUpdates =
                requiredUpdates.Where(u => !u.update.IsCoherencyUpdate).ToList();
            // Should max one coherency update
            (UpdateAssetsParameters update, List<DependencyUpdate> deps) coherencyUpdate =
                requiredUpdates.Where(u => u.update.IsCoherencyUpdate).SingleOrDefault();

            IRemote remote = await remoteFactory.GetRemoteAsync(targetRepository, Logger);

            // To keep a PR to as few commits as possible, if the number of
            // non-coherency updates is 1 then combine coherency updates with those.
            // Otherwise, put all coherency updates in a separate commit.
            bool combineCoherencyWithNonCoherency = (nonCoherencyUpdates.Count == 1);
            foreach ((UpdateAssetsParameters update, List<DependencyUpdate> deps) in nonCoherencyUpdates)
            {
                var message = new StringBuilder();
                List<DependencyUpdate> dependenciesToCommit = deps;
                await CalculateCommitMessage(update, deps, message);


                if (combineCoherencyWithNonCoherency && coherencyUpdate.update != null)
                {
                    await CalculateCommitMessage(coherencyUpdate.update, coherencyUpdate.deps, message);
                    dependenciesToCommit.AddRange(coherencyUpdate.deps);
                }

                List<GitFile> committedFiles = await remote.CommitUpdatesAsync(targetRepository, newBranchName, remoteFactory,
                    dependenciesToCommit.Select(du => du.To).ToList(), message.ToString());
            }

            // If the coherency update wasn't combined, then
            // add it now
            if (!combineCoherencyWithNonCoherency && coherencyUpdate.update != null)
            {
                var message = new StringBuilder();
                await CalculateCommitMessage(coherencyUpdate.update, coherencyUpdate.deps, message);

                await remote.CommitUpdatesAsync(targetRepository, newBranchName, remoteFactory,
                    coherencyUpdate.deps.Select(du => du.To).ToList(), message.ToString());
            }
        }

        private async Task CalculateCommitMessage(UpdateAssetsParameters update, List<DependencyUpdate> deps, StringBuilder message)
        {
            if (update.IsCoherencyUpdate)
            {
                message.AppendLine("Dependency coherency updates");
                message.AppendLine();
                message.AppendLine(string.Join(",", deps.Select(p => p.To.Name)));
                message.AppendLine($" From Version {deps[0].From.Version} -> To Version {deps[0].To.Version} (parent: {deps[0].To.CoherentParentDependencyName}");
            }
            else
            {
                string sourceRepository = update.SourceRepo;
                Build build = await GetBuildAsync(update.BuildId);
                message.AppendLine($"Update dependencies from {sourceRepository} build {build.AzureDevOpsBuildNumber}");
                message.AppendLine();
                message.AppendLine(string.Join(" , ", deps.Select(p => p.To.Name)));
                message.AppendLine($" From Version {deps[0].From.Version} -> To Version {deps[0].To.Version}");
            }

            message.AppendLine();
        }

        /// <summary>
        /// Look at the last 5 builds registered in the BAR for the repo and branch. 
        /// If they were all queued from the same definition we have good confidence that's the official build we should be queueing.
        /// </summary>
        /// <param name="repoUri"></param>
        /// <returns></returns>
        private (string account, string project, int definitionId) InferOfficialBuildDefinitionForRepo(string repoUri, string branch)
        {
            List<Build> buildsForRepoAndBranch = Context.Builds.Where(
                b => (b.GitHubRepository.Equals(repoUri, StringComparison.OrdinalIgnoreCase) 
                    || b.AzureDevOpsRepository.Equals(repoUri, StringComparison.OrdinalIgnoreCase))
                && (b.GitHubBranch.Equals(branch, StringComparison.OrdinalIgnoreCase) 
                    || b.AzureDevOpsBranch.Equals($"refs/heads/{branch}", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(b=> b.DateProduced)
                .Take(5)
                .ToList();

            if (buildsForRepoAndBranch.Any())
            {
                Build first = buildsForRepoAndBranch.First();
                if (!buildsForRepoAndBranch.Any(
                    b=> !b.AzureDevOpsBuildDefinitionId.HasValue
                    && b.AzureDevOpsBuildDefinitionId != first.AzureDevOpsBuildDefinitionId))
                {
                    return (first.AzureDevOpsAccount, first.AzureDevOpsBranch, first.AzureDevOpsBuildDefinitionId.Value);
                }
            }

            Logger.LogInformation($"Unable to infer the build definition for repo {repoUri}");
            return (string.Empty, string.Empty, -1);
        }

        
        // TODO: Extract this guy to a base class for PullRequest and MergeActor, right now it can't be extracted as a helper
        private async Task<List<(UpdateAssetsParameters update, List<DependencyUpdate> deps)>> GetRequiredUpdates(
            List<UpdateAssetsParameters> updates,
            IRemoteFactory remoteFactory,
            string targetRepository,
            string branch)
        {
            // Get a remote factory for the target repo
            IRemote darc = await remoteFactory.GetRemoteAsync(targetRepository, Logger);

            var requiredUpdates = new List<(UpdateAssetsParameters update, List<DependencyUpdate> deps)>();
            // Existing details 
            List<DependencyDetail> existingDependencies = (await darc.GetDependenciesAsync(targetRepository, branch)).ToList();

            foreach (UpdateAssetsParameters update in updates)
            {
                IEnumerable<AssetData> assetData = update.Assets.Select(
                    a => new AssetData(false)
                    {
                        Name = a.Name,
                        Version = a.Version
                    });
                // Retrieve the source of the assets

                List<DependencyUpdate> dependenciesToUpdate = await darc.GetRequiredNonCoherencyUpdatesAsync(
                    update.SourceRepo,
                    update.SourceSha,
                    assetData,
                    existingDependencies);

                if (dependenciesToUpdate.Count < 1)
                {
                    // No dependencies need to be updated.
                    await UpdateSubscriptionsForMergedUpdateAsync(
                        new List<PreMergeOfficialBuildParameters>
                        {
                            new PreMergeOfficialBuildParameters
                            {
                                SubscriptionId = update.SubscriptionId,
                                BuildId = update.BuildId
                            }
                        });
                    continue;
                }

                // Update the existing details list
                foreach (DependencyUpdate dependencyUpdate in dependenciesToUpdate)
                {
                    existingDependencies.Remove(dependencyUpdate.From);
                    existingDependencies.Add(dependencyUpdate.To);
                }
                requiredUpdates.Add((update, dependenciesToUpdate));
            }

            // Once we have applied all of non coherent updates, then we need to run a coherency check on the dependencies.
            // First, we'll try it with strict mode; failing that an attempt with legacy mode.
            List<DependencyUpdate> coherencyUpdates = new List<DependencyUpdate>();
            bool strictCheckFailed = false;
            try
            {
                coherencyUpdates = await darc.GetRequiredCoherencyUpdatesAsync(existingDependencies, remoteFactory, CoherencyMode.Strict);
            }
            catch (DarcCoherencyException)
            {
                Logger.LogInformation("Failed attempting strict coherency update on branch '{strictCoherencyFailedBranch}' of repo '{strictCoherencyFailedRepo}'.  Will now retry in Legacy mode.",
                     branch, targetRepository);
                strictCheckFailed = true;
            }
            if (strictCheckFailed)
            {
                coherencyUpdates = await darc.GetRequiredCoherencyUpdatesAsync(existingDependencies, remoteFactory, CoherencyMode.Legacy);
                // If the above call didn't throw, that means legacy worked while strict did not.
                // Send a special trace that can be easily queried later from App Insights, to gauge when everything can handle Strict mode.
                Logger.LogInformation("Strict coherency update failed, but Legacy update worked for branch '{strictCoherencyFailedBranch}' of repo '{strictCoherencyFailedRepo}'.",
                     branch, targetRepository);
            }

            if (coherencyUpdates.Any())
            {
                // For the update asset parameters, we don't have any information on the source of the update,
                // since coherency can be run even without any updates.
                UpdateAssetsParameters coherencyUpdateParameters = new UpdateAssetsParameters
                {
                    IsCoherencyUpdate = true
                };
                requiredUpdates.Add((coherencyUpdateParameters, coherencyUpdates.ToList()));
            }

            return requiredUpdates;
        }
    }
}
