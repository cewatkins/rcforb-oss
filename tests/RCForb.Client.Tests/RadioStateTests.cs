using RCForb.Client.State;

namespace RCForb.Client.Tests;

/// <summary>
/// State parsing is where a guest learns what it is listening to. The tests use lines
/// captured from a real v0.7 server, with the transports' namespaces as they arrive.
/// </summary>
public class RadioStateTests
{
    [Fact] // SPEC 04: the server speaks post:: and the client uses radio:: internally.
    public void Both_namespaces_populate_the_same_state()
    {
        var state = new RadioStateStore();

        state.Apply("post::radio::K3");
        state.Apply("radio::driver::Elecraft (1.97)");

        Assert.Equal("K3", state.RadioName);
        Assert.Equal("Elecraft (1.97)", state.Driver);
    }

    [Fact] // SPEC-SESS-015: the first chat line latches readiness.
    public void Readiness_latches_on_chat()
    {
        var state = new RadioStateStore();

        state.Apply("chat::System::2026-10-28 13:44:18 +01:00::Hello%20there");

        Assert.True(state.IsReady);
        Assert.Equal("Hello there", state.LastChat!.Text);
        Assert.Equal("System", state.LastChat.Sender);
    }

    [Fact] // SPEC-SESS-015: state-posted latches readiness in either namespace.
    public void Readiness_latches_on_state_posted()
    {
        var state = new RadioStateStore();
        state.Apply("post::state-posted");
        Assert.True(state.IsReady);
    }

    [Fact] // SPEC-SESS-016: readiness is a latch, not a re-evaluated predicate.
    public void Readiness_does_not_clear_when_later_lines_arrive()
    {
        var state = new RadioStateStore();
        state.Apply("post::state-posted");
        state.Apply("radio::state-posted");

        Assert.True(state.IsReady);
    }

    [Fact]
    public void Values_are_split_at_the_first_key_separator_only()
    {
        var state = new RadioStateStore();

        state.Apply("post::message::CW::CQ CQ :: DE SP7VTQ");

        Assert.Equal("CQ CQ :: DE SP7VTQ", state.Messages["CW"]);
    }

    [Fact]
    public void A_key_with_an_empty_value_is_kept()
    {
        var state = new RadioStateStore();

        state.Apply("post::message::Decoder::");

        Assert.True(state.Messages.ContainsKey("Decoder"));
        Assert.Equal("", state.Messages["Decoder"]);
    }

    [Fact]
    public void Frequency_and_mode_come_from_their_own_bags()
    {
        var state = new RadioStateStore();

        state.Apply("post::frequency::28074000");
        state.Apply("post::frequencyB::28090400");
        state.Apply("post::dropdown::Mode::USB");
        state.Apply("post::dropdown::Filter::FL1");
        state.Apply("post::smeter::S6,6,21");

        Assert.Equal(28_074_000, state.FrequencyHz);
        Assert.Equal(28_090_400, state.FrequencyBHz);
        Assert.Equal("USB", state.Dropdowns["Mode"]);
        Assert.Equal("S6,6,21", state.SMeter);
    }

    [Fact] // SPEC-STATE-003: the wire's smeterB is stored under the canonical lower-case slot.
    public void The_second_meter_keeps_its_own_bag()
    {
        var state = new RadioStateStore();

        state.Apply("post::smeterB::S2,2,21");

        Assert.Equal("S2,2,21", state.SMeterB);
        Assert.Equal("", state.SMeter);
    }

    [Fact] // SPEC-STATE-005: keys outside the schema are retained, not dropped.
    public void Unknown_keys_are_retained()
    {
        var state = new RadioStateStore();

        state.Apply("post::vendor-future-key::whatever");

        Assert.Equal("whatever", state.Extension["vendor-future-key"]);
    }

    [Fact]
    public void Duplicate_assignments_are_idempotent()
    {
        var state = new RadioStateStore();

        for (var i = 0; i < 3; i++)
        {
            state.Apply("post::button::Split::1");
            state.Apply("post::frequency::14074000");
        }

        Assert.Equal("1", state.Buttons["Split"]);
        Assert.Equal(14_074_000, state.FrequencyHz);
    }

    [Fact]
    public void The_power_off_sentinel_stays_readable()
    {
        var state = new RadioStateStore();

        state.Apply("post::frequency::PWROFF");

        Assert.Null(state.FrequencyHz);
        Assert.Equal("PWROFF", RadioStateStore.FormatHz(state.FrequencyText));
    }

    [Fact]
    public void Chat_line_breaks_are_flattened_for_display()
    {
        var state = new RadioStateStore();

        state.Apply("chat::System::now::Line%20one%0D%0ALine%20two");

        Assert.Equal("Line one Line two", state.LastChat!.Text);
    }

    [Fact] // A UI reads the store from another thread while the control pump writes it.
    public async Task Snapshot_can_be_taken_while_lines_are_being_applied()
    {
        var state = new RadioStateStore();
        using var stop = new CancellationTokenSource();

        var pump = Task.Run(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                state.Apply($"post::smeter::S9,{i++ % 21},21");
                state.Apply($"post::extra{i % 5}::value{i}");
            }
        });

        try
        {
            // Enumerating the live bags here would throw "Collection was modified"; the
            // snapshot copies them under the store's lock instead.
            for (var i = 0; i < 2_000; i++)
            {
                var snapshot = state.Snapshot();
                _ = snapshot.Extension.Count + snapshot.Dropdowns.Count;
                Assert.InRange(snapshot.LinesApplied, 0, int.MaxValue);
            }
        }
        finally
        {
            stop.Cancel();
            await pump;
        }
    }

    [Fact] // SPEC 04 row 89: post::user_in adds a roster entry (uid defaults to user).
    public void Post_user_in_adds_a_roster_entry()
    {
        var state = new RadioStateStore();

        state.Apply("post::user_in::testuser");

        Assert.Single(state.Roster);
        Assert.Equal("testuser", state.Roster[0].User);
        Assert.Equal("testuser", state.Roster[0].Uid);
        Assert.Single(state.Snapshot().Roster);
    }

    [Fact] // SPEC 04 row 90: post::user_out removes every row matching the uid.
    public void Post_user_out_removes_roster_entries_by_uid()
    {
        var state = new RadioStateStore();

        state.Apply("post::user_in::testuser");
        state.Apply("post::user_in::testuser");
        state.Apply("post::user_out::testuser");

        Assert.Empty(state.Roster);
    }

    [Fact] // SPEC 04 row 91: server::user_login removes then re-adds keeping the uid.
    public void Server_user_login_renames_while_keeping_uid()
    {
        var state = new RadioStateStore();

        state.Apply("server::user_in::oldname::uid-42");
        state.Apply("server::user_login::newname::uid-42");

        Assert.Single(state.Roster);
        Assert.Equal("newname", state.Roster[0].User);
        Assert.Equal("uid-42", state.Roster[0].Uid);
    }

    [Fact] // SPEC-CMD-059: removal is by uid, not display name.
    public void Roster_removal_is_keyed_by_uid_not_display_name()
    {
        var state = new RadioStateStore();

        state.Apply("server::user_in::alice::uid-a");
        state.Apply("server::user_in::alice::uid-b");
        state.Apply("server::user_out::alice::uid-a");

        Assert.Single(state.Roster);
        Assert.Equal("uid-b", state.Roster[0].Uid);
    }

    [Fact] // SPEC 04: dialog and chat text arrive percent-encoded and are decoded for display.
    public void Percent_escapes_are_decoded_and_line_breaks_flattened()
    {
        Assert.Equal("No TX during rotation", ProtocolText.Decode("No%20TX%20during%20rotation"));
        Assert.Equal("one two", ProtocolText.Decode("one%0D%0Atwo"));
        Assert.Equal("%ZZ stays", ProtocolText.Decode("%ZZ%20stays"));
    }
}
