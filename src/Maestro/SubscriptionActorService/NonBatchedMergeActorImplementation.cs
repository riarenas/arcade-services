// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Maestro.Contracts;
using Maestro.Data;
using Maestro.Data.Models;
using Microsoft.DotNet.DarcLib;
using Microsoft.DotNet.ServiceFabric.ServiceHost;
using Microsoft.DotNet.ServiceFabric.ServiceHost.Actors;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Actors;
using Microsoft.ServiceFabric.Actors.Runtime;

namespace SubscriptionActorService
{
    /// <summary>
    ///     A <see cref="MergeActorImplementation" /> that reads its Target information from a
    ///     non-batched subscription object
    /// </summary>
    public class NonBatchedMergeActorImplementation : MergeActorImplementation
    {
        private readonly Lazy<Task<Subscription>> _lazySubscription;

        public NonBatchedMergeActorImplementation(
            ActorId id,
            IReminderManager reminders,
            IActorStateManager stateManager,
            BuildAssetRegistryContext context,
            IRemoteFactory darcFactory,
            ILoggerFactory loggerFactory,
            IActionRunner actionRunner,
            IActorProxyFactory<ISubscriptionActor> subscriptionActorFactory) : base(
            id,
            reminders,
            stateManager,
            context,
            darcFactory,
            loggerFactory,
            actionRunner,
            subscriptionActorFactory)
        {
            _lazySubscription = new Lazy<Task<Subscription>>(RetrieveSubscription);
        }

        public Guid SubscriptionId => Id.GetGuidId();

        private async Task<Subscription> RetrieveSubscription()
        {
            Subscription subscription = await Context.Subscriptions.FindAsync(SubscriptionId);
            if (subscription == null)
            {
                await Reminders.TryUnregisterReminderAsync(OfficialBuildRunning);
                await Reminders.TryUnregisterReminderAsync(ChangesMerged);
                await StateManager.TryRemoveStateAsync(PreMergeOfficialBuildKey);
                await StateManager.TryRemoveStateAsync(PostMergeOfficialBuildKey);
                await StateManager.TryRemoveStateAsync(UpdatesKey);

                throw new SubscriptionException($"Subscription '{SubscriptionId}' was not found...");
            }

            return subscription;
        }

        private Task<Subscription> GetSubscription()
        {
            return _lazySubscription.Value;
        }

        protected override async Task<(string repository, string branch)> GetTargetAsync()
        {
            Subscription subscription = await GetSubscription();
            return (subscription.TargetRepository, subscription.TargetBranch);
        }
    }
}
