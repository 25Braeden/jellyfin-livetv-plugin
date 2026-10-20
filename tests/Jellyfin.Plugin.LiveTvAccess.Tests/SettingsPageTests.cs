using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LiveTvAccess.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>Step 3: the admin settings page (embedded configPage, standard plugin page mechanism).</summary>
public class SettingsPageTests
{
    private static (PluginPageInfo Page, string Html) Page()
    {
        var w = new World();
        var pages = Assert.IsAssignableFrom<IHasWebPages>(w.Plugin).GetPages().ToArray();
        var page = Assert.Single(pages);
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return (page, reader.ReadToEnd());
    }

    [Fact]
    public void Page_IsEmbedded_AndRegisteredUnderThePluginName()
    {
        var (page, html) = Page();
        Assert.Equal("LiveTvAccess", page.Name);
        Assert.Contains("pluginConfigurationPage", html, StringComparison.Ordinal);
        Assert.Contains("data-role=\"page\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_SavesThroughTheNormalPluginConfigPath()
    {
        var (_, html) = Page();
        Assert.Contains("4d5aa36c-9164-4f4d-ba7f-0fa6c25bfd28", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ApiClient.getPluginConfiguration", html, StringComparison.Ordinal);
        Assert.Contains("ApiClient.updatePluginConfiguration", html, StringComparison.Ordinal);
        Assert.Contains("LiveTvAccess/Status", html, StringComparison.Ordinal);
        Assert.Contains("ApiClient.getUsers", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_LoadsNoExternalResources()
    {
        var (_, html) = Page();
        Assert.DoesNotMatch(new Regex(@"<(script|link|img|iframe)[^>]+(src|href)\s*=", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"(https?:)?//[a-z0-9.-]+\.[a-z]{2,}/", RegexOptions.IgnoreCase), html);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fetch(", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_NeverInjectsDataAsHtml()
    {
        var (_, html) = Page();

        // Every value goes in through textContent / value; nodes are cleared with removeChild.
        Assert.DoesNotContain("innerHTML", html, StringComparison.Ordinal);
        Assert.DoesNotContain("insertAdjacentHTML", html, StringComparison.Ordinal);
        Assert.DoesNotContain("outerHTML", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PagePayload_UserPickedById_StaleName_IsRefreshed_HistoryKept()
    {
        var w = new World();
        w.Plugin.Configuration.OwnedTagHistory = ["livetv-old"];

        // What the page sends: the user id from the dropdown (its display name may be stale), no history.
        var saved = new PluginConfiguration
        {
            Tuners = [new TunerConfig { Key = "tuner1", Url = World.Tuner1Url }],
            Users = [new UserTunerConfig { UserId = w.UserA.Id, UserName = "renamed-meanwhile", TunerKey = "tuner1" }],
            OwnedTagHistory = [],
        };
        w.Plugin.UpdateConfiguration(saved);

        var entry = Assert.Single(w.Plugin.Configuration.Users);
        Assert.Equal(w.UserA.Id, entry.UserId);
        Assert.Equal(w.UserA.Username, entry.UserName);
        Assert.Contains("livetv-old", w.Plugin.Configuration.OwnedTagHistory);
    }
}
