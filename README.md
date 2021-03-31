# Arcade Services

## MergeActor feature branch

This branch contains the in-progress changes to introduce the PR-less dependency flow as specified in https://github.com/dotnet/core-eng/blob/main/Documentation/Project-Docs/Dependency%20Flow/improved-dependency-flow.md

The bulk of the changes are in the MergeActor subscription Actor type that lives in the https://github.com/dotnet/arcade-services/tree/riarenas/merge-actor/src/Maestro/SubscriptionActorService project.

A full diff of the changes can be found by going to https://github.com/dotnet/arcade-services/compare/riarenas/merge-actor?expand=1

The remaining work revolves around the following areas:

* There's a bug somewhere where the SubscriptionActorService will pick the wrong actor type to instantiate.
* Sometimes the actor loses its state, so it's unable to act upon the reminders that it receives. I haven't found the code path that causes this to happen, but it can result in losing track of the Official build that gets queued with the dependency updates.
* Implementing a batched version of the MergeActor to handle batched subscriptions.
* Scenario tests for the dependency flow without a pull request scenario.
* Falling back to instantiate a PullRequestActor when the MergeActor fails.
