// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Maestro.Data;
using Maestro.Data.Models;
using System;
using System.Threading.Tasks;

namespace SubscriptionActorService
{
    public static class DependencyUpdateActorUtils
    {
        public static async Task<RepositoryBranchUpdate> GetRepositoryBranchUpdate(BuildAssetRegistryContext context, string repo, string branch)
        {
            RepositoryBranchUpdate update = await context.RepositoryBranchUpdates.FindAsync(repo, branch);
            if (update == null)
            {
                RepositoryBranch repoBranch = await GetRepositoryBranch(context, repo, branch);
                context.RepositoryBranchUpdates.Add(
                    update = new RepositoryBranchUpdate { RepositoryBranch = repoBranch });
            }
            else
            {
                context.RepositoryBranchUpdates.Update(update);
            }

            return update;
        }

        private static async Task<RepositoryBranch> GetRepositoryBranch(BuildAssetRegistryContext context, string repo, string branch)
        {
            RepositoryBranch repoBranch = await context.RepositoryBranches.FindAsync(repo, branch);
            if (repoBranch == null)
            {
                context.RepositoryBranches.Add(
                    repoBranch = new RepositoryBranch
                    {
                        RepositoryName = repo,
                        BranchName = branch
                    });
            }
            else
            {
                context.RepositoryBranches.Update(repoBranch);
            }

            return repoBranch;
        }

        private static async Task<string> GetSourceRepositoryAsync(BuildAssetRegistryContext context, Guid subscriptionId)
        {
            Subscription subscription = await context.Subscriptions.FindAsync(subscriptionId);
            return subscription?.SourceRepository;
        }
    }
}
