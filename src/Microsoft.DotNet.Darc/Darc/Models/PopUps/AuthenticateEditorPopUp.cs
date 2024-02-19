// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.DotNet.Darc.Helpers;
using Microsoft.Extensions.Logging;

namespace Microsoft.DotNet.Darc.Models;

internal class AuthenticateEditorPopUp : EditorPopUp
{
    private readonly ILogger _logger;

    private const string BarPasswordElement = "bar_password";
    private const string GithubTokenElement = "github_token";
    private const string AzureDevOpsTokenElement = "azure_devops_token";
    private const string BarBaseUriElement = "build_asset_registry_base_uri";

    public AuthenticateEditorPopUp(string path, ILogger logger)
        : base(path)
    {
        _logger = logger;
        try
        {
            // Load current settings
            settings = LocalSettings.LoadSettingsFile();
        }
        catch (Exception e)
        {
            // Failed to load the settings file.  Quite possible it just doesn't exist.
            // In this case, just initialize the settings to empty
            _logger.LogTrace($"Couldn't load or locate the settings file ({e.Message}).  Initializing empty settings file");
            settings = new LocalSettings();
        }

        // Initialize line contents.
        Contents =
        [
            new("Create new BAR tokens at https://maestro.dot.net/Account/Tokens", isComment: true),
            new($"{BarPasswordElement}={GetCurrentSettingForDisplay(settings.BuildAssetRegistryPassword, string.Empty, true)}"),
            new("Create new GitHub personal access tokens at https://github.com/settings/tokens (no scopes needed but needs SSO enabled on the PAT)", isComment: true),
            new($"{GithubTokenElement}={GetCurrentSettingForDisplay(settings.GitHubToken, string.Empty, true)}"),
            new("Create new Azure Dev Ops tokens using the PatGeneratorTool https://dev.azure.com/dnceng/public/_artifacts/feed/dotnet-eng/NuGet/Microsoft.DncEng.PatGeneratorTool", isComment: true),
            new("with the `dotnet pat-generator --scopes build_execute code --organizations dnceng devdiv --expires-in 180` command", isComment: true),
            new($"{AzureDevOpsTokenElement}={GetCurrentSettingForDisplay(settings.AzureDevOpsToken, string.Empty, true)}"),
            new($"{BarBaseUriElement}={GetCurrentSettingForDisplay(settings.BuildAssetRegistryBaseUri, "<alternate build asset registry uri if needed, otherwise leave as is>", false)}"),
            new(""),
            new("Storing the required settings...", true),
            new($"Set elements above depending on what you need", true),
        ];
    }

    public LocalSettings settings { get; set; }

    public override int ProcessContents(IList<Line> contents)
    {
        foreach (Line line in contents)
        {
            string[] keyValue = line.Text.Split("=");

            switch (keyValue[0])
            {
                case BarPasswordElement:
                    settings.BuildAssetRegistryPassword = ParseSetting(keyValue[1], settings.BuildAssetRegistryPassword, true);
                    break;
                case GithubTokenElement:
                    settings.GitHubToken = ParseSetting(keyValue[1], settings.GitHubToken, true);
                    break;
                case AzureDevOpsTokenElement:
                    settings.AzureDevOpsToken = ParseSetting(keyValue[1], settings.AzureDevOpsToken, true);
                    break;
                case BarBaseUriElement:
                    settings.BuildAssetRegistryBaseUri = ParseSetting(keyValue[1], settings.BuildAssetRegistryBaseUri, false);
                    break;
                default:
                    _logger.LogWarning($"'{keyValue[0]}' is an unknown field in the authentication scope");
                    break;
            }
        }

        return settings.SaveSettingsFile(_logger);
    }
}
