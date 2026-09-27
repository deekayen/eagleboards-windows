namespace EagleBoards.App;

/// <summary>The commented config.properties written into a new data folder.</summary>
internal static class DefaultConfig
{
    public const string Text = """
        # Eagle Board Scheduler configuration
        # Edit the values after each '='. Lines starting with # are comments.
        # The Settings window edits these too (and rewrites the file without comments).

        Type=CONFIG
        ID=DEFAULT
        Name=DEFAULT

        # How often (in seconds) the screens reload their data.
        RefreshTimeSecs=30

        # How long the board may spend convening before the scout is brought in --
        # the members read the application, references and project workbook first.
        # A single cap, not a target: there is no yellow stage.
        ConveneRedMins=30

        # Room-card warning thresholds, in minutes since the scout was brought in
        # ("Start Review"). The card's timer shows running long at the "Yellow" time
        # and overdue at the "Red" time. Set per board type.
        ProjectYellowMins=25
        ProjectRedMins=40
        FinalYellowMins=30
        FinalRedMins=45

        # Status colors are not settings: every version draws them from one
        # palette, readable with color blindness, in light and dark.

        """;
}
