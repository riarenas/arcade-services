# Arcade Services

## MergeActor feature branch

This branch contains the in-progress changes to introduce the PR-less dependency flow as specified in https://github.com/dotnet/core-eng/blob/main/Documentation/Project-Docs/Dependency%20Flow/improved-dependency-flow.md

The bulk of the changes are in the MergeActor subscription Actor type that lives in the https://github.com/dotnet/arcade-services/tree/riarenas/merge-actor/src/Maestro/SubscriptionActorService project.

A full diff of the changes can be found by going to https://github.com/dotnet/arcade-services/compare/riarenas/merge-actor?expand=1

The MergeActor is able to find the official build for a target repository, queue a test official build with changes that have been pushed to a temporary darc-GUID branch, and attempt to merge the temporary branch into the target branch for the given subscription if the build succeeds. Once the change is merged, the build is added to the target channel, and the actor scans the official build definition for the target repository, tries to find a build queued for the merge commit in the target branch and cancels it so that we don't run two official builds with the same exact changes.

The remaining work revolves around the following areas:

* There's a bug somewhere where the SubscriptionActorService will pick the wrong actor type to instantiate.
* Sometimes the actors lose their state, so it's unable to act upon the reminders that it receives. I haven't found the code path that causes this to happen, but it has resulted in both the PullRequestActor and the MergeActor to both lose state. I suspect something wrong with the communication in Service Fabric.
* Implementing a batched version of the MergeActor to handle batched subscriptions.
* Scenario tests for the dependency flow without a pull request scenario.
* Falling back to instantiate a PullRequestActor when the MergeActor fails at any point.
