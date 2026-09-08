using Dalamud.Plugin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services;

public sealed class ExternalPluginRestartAdvisor : DisposableMediatorSubscriberBase, IHostedService
{
    private static readonly string[] WatchedPlugins = ["Penumbra", "Glamourer"];

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Dictionary<string, Version> _knownVersions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unloadedWhileKnown = new(StringComparer.Ordinal);
    private readonly HashSet<string> _alreadyAdvised = new(StringComparer.Ordinal);

    public ExternalPluginRestartAdvisor(ILogger<ExternalPluginRestartAdvisor> logger, MareMediator mediator,
        IDalamudPluginInterface pluginInterface) : base(logger, mediator)
    {
        _pluginInterface = pluginInterface;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var name in WatchedPlugins)
        {
            var state = PluginWatcherService.GetInitialPluginState(_pluginInterface, name);
            if (state is { IsLoaded: true })
            {
                _knownVersions[name] = state.Version;
            }
            
            Mediator.SubscribeKeyed<PluginChangeMessage>(this, name, OnPluginChanged);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Mediator.UnsubscribeAll(this);
        return Task.CompletedTask;
    }

    private void OnPluginChanged(PluginChangeMessage msg)
    {
        Logger.LogDebug("PluginChange {plugin} v{version} loaded={loaded}", msg.InternalName, msg.Version, msg.IsLoaded);

        if (!_knownVersions.TryGetValue(msg.InternalName, out var knownVersion))
        {
            if (msg.IsLoaded) _knownVersions[msg.InternalName] = msg.Version;
            return;
        }

        if (!msg.IsLoaded)
        {
            _unloadedWhileKnown.Add(msg.InternalName);
            return;
        }

        bool updated = msg.Version != knownVersion;
        bool reloaded = _unloadedWhileKnown.Remove(msg.InternalName);
        _knownVersions[msg.InternalName] = msg.Version;

        if (!updated && !reloaded) return;
        if (!_alreadyAdvised.Add(msg.InternalName)) return;

        Logger.LogWarning("{plugin} a ete {reason} pendant la session (version {version}) : relance d'UmbraSync recommandee",
            msg.InternalName, updated ? "mis a jour" : "recharge", msg.Version);

        Mediator.Publish(new DualNotificationMessage(
            Loc.Get("PluginRestart.Title"),
            string.Format(CultureInfo.CurrentCulture, Loc.Get("PluginRestart.Body"), msg.InternalName),
            NotificationType.Warning,
            TimeSpan.FromSeconds(15),
            ForceBoth: true));
    }
}
