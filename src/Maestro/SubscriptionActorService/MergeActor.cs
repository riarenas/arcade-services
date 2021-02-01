// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Maestro.Contracts;
using Maestro.Data;
using Microsoft.DotNet.DarcLib;
using Microsoft.DotNet.ServiceFabric.ServiceHost;
using Microsoft.DotNet.ServiceFabric.ServiceHost.Actors;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Runtime;
using Asset = Maestro.Contracts.Asset;

namespace SubscriptionActorService
{
    namespace unused
    {
        // class needed to appease service fabric build time generation of actor code
        [StatePersistence(StatePersistence.Persisted)]
        [ActorService(Name = "MergeActorService")]
        public class MergeActor : Actor, IPullRequestActor, IRemindable
        {
            public MergeActor(ActorService actorService, ActorId actorId) : base(actorService, actorId)
            {
            }

            public Task<string> RunActionAsync(string method, string arguments)
            {
                throw new NotImplementedException();
            }

            public Task UpdateAssetsAsync(Guid subscriptionId, int buildId, string sourceRepo, string sourceSha, List<Asset> assets)
            {
                throw new NotImplementedException();
            }

            public Task ReceiveReminderAsync(string reminderName, byte[] state, TimeSpan dueTime, TimeSpan period)
            {
                throw new NotImplementedException();
            }
        }
    }

    /// <summary>
    ///     A service fabric actor implementation that is responsible for merging dependency
    ///     updates to the target branches after running an official build
    /// </summary>
    public class MergeActor : IPullRequestActor, IRemindable, IActionTracker, IActorImplementation
    {
        private readonly BuildAssetRegistryContext _context;
        private readonly IRemoteFactory _darcFactory;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IActionRunner _actionRunner;
        private readonly IActorProxyFactory<ISubscriptionActor> _subscriptionActorFactory;

        /// <summary>
        ///     Creates a new MergeActor
        /// </summary>
        /// <param name="id">
        ///     The actor id for this actor.
        ///     If it is a <see cref="Guid" /> actor id, then it is required to be the id of a non-batched subscription in the
        ///     database
        ///     If it is a <see cref="string" /> actor id, then it MUST be an actor id created with
        ///     <see cref="PullRequestActorId.Create(string, string)" /> for use with all subscriptions targeting the specified
        ///     repository and branch.
        /// </param>
        /// <param name="provider"></param>
        public MergeActor(
            BuildAssetRegistryContext context,
            IRemoteFactory darcFactory,
            ILoggerFactory loggerFactory,
            IActionRunner actionRunner,
            IActorProxyFactory<ISubscriptionActor> subscriptionActorFactory)
        {
            _context = context;
            _darcFactory = darcFactory;
            _loggerFactory = loggerFactory;
            _actionRunner = actionRunner;
            _subscriptionActorFactory = subscriptionActorFactory;
        }

        public void Initialize(ActorId actorId, IActorStateManager stateManager, IReminderManager reminderManager)
        {
            Implementation = GetImplementation(actorId, stateManager, reminderManager);
        }

        private MergeActorImplementation GetImplementation(ActorId actorId, IActorStateManager stateManager, IReminderManager reminderManager)
        {
            switch (actorId.Kind)
            {
                case ActorIdKind.Guid:
                    return new NonBatchedMergeActorImplementation(actorId,
                        reminderManager,
                        stateManager,
                        _context,
                        _darcFactory,
                        _loggerFactory,
                        _actionRunner,
                        _subscriptionActorFactory);

                /* No batched MergeActor yet, but soon.
                case ActorIdKind.String:
                    return new BatchedMergeActorImplementation(actorId,
                        reminderManager,
                        stateManager,
                        _context,
                        _darcFactory,
                        _loggerFactory,
                        _actionRunner,
                        _subscriptionActorFactory);
                */ 
                default:
                    throw new NotSupportedException("Only actorId's of type Guid are supported");
            }
        }

        public MergeActorImplementation Implementation { get; private set; }

        public Task TrackSuccessfulAction(string action, string result)
        {
            return Implementation.TrackSuccessfulAction(action, result);
        }

        public Task TrackFailedAction(string action, string result, string method, string arguments)
        {
            return Implementation.TrackFailedAction(action, result, method, arguments);
        }

        public Task<string> RunActionAsync(string method, string arguments)
        {
            return Implementation.RunActionAsync(method, arguments);
        }

        public Task UpdateAssetsAsync(Guid subscriptionId, int buildId, string sourceRepo, string sourceSha, List<Asset> assets)
        {
            return Implementation.UpdateAssetsAsync(subscriptionId, buildId, sourceRepo, sourceSha, assets);
        }

        public async Task ReceiveReminderAsync(string reminderName, byte[] state, TimeSpan dueTime, TimeSpan period)
        {
            if (reminderName == MergeActorImplementation.OfficialBuildRunning)
            {
                await Implementation.WaitForOfficialBuild();
            }
            else if (reminderName == MergeActorImplementation.ChangesMerged)
            {
                await Implementation.CancelOfficialBuild();
            }
            else
            {
                throw new ReminderNotFoundException(reminderName);
            }
        }
    }
}
