using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EagleBoards.App;

/// <summary>
/// Speech for screen readers with nothing put on screen, so D-14 still holds:
/// a step that worked, a room running long, a page shown.
/// </summary>
internal static class Announcer
{
    /// <summary>Say <paramref name="text"/>, raised from <paramref name="from"/> (an element with a peer: a window, a list, a text).</summary>
    public static void Say(UIElement from, string text, string activity, bool important = false)
    {
        if (text.Length > 0 && UIElementAutomationPeer.CreatePeerForElement(from) is { } peer)
        {
            peer.RaiseNotificationEvent(
                important ? AutomationNotificationKind.ActionCompleted : AutomationNotificationKind.Other,
                important ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
                text, activity);
        }
    }

    /// <summary>A live region's text changed (AutomationProperties.LiveSetting): have it read. WPF reads nothing without this.</summary>
    public static void LiveRegionChanged(UIElement element) =>
        UIElementAutomationPeer.CreatePeerForElement(element)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
}
