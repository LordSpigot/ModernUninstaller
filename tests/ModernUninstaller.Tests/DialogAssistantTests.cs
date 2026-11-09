using ModernUninstaller.Models;
using ModernUninstaller.Services;
using ModernUninstaller.Services.Ml;
using Xunit;

namespace ModernUninstaller.Tests;

public class DialogAssistantTests
{
    static readonly Lazy<DialogModel> Trained = new(() =>
    {
        var m = new DialogModel(Lexicon.FromJson(DialogModel.ReadResource("lexicon.json")));
        m.Train(DialogModel.ParseSamples(DialogModel.ReadResource("seed-dialogs.json")));
        return m;
    });

    static DialogControl B(string l) => new(ControlKind.Button, l);
    static DialogSnapshot W(string title, string text, params DialogControl[] c) => new(title, new[] { text }, c);

    static string? Clicked(DialogSnapshot w)
    {
        var d = Trained.Value.Decide(w);
        return d.Action == AssistAction.Click ? w.Controls[d.Index].Label : null;
    }

    [Fact]
    public void Seed_is_learned_without_wrong_clicks()
    {
        var samples = DialogModel.ParseSamples(DialogModel.ReadResource("seed-dialogs.json"));
        Assert.True(samples.Count >= 100);
        int wrong = 0;
        foreach (var s in samples)
        {
            var d = Trained.Value.Decide(s.Window);
            if (d.Action == AssistAction.Click && d.Index != s.Target) wrong++;
            if (d.Action == AssistAction.Wait && s.Target != -1) wrong++;
        }
        Assert.True(wrong <= 2, $"{wrong} falsche Entscheidungen auf dem Startwissen");
    }

    [Theory]
    [InlineData("ZebraNotes deinstallieren", "Möchten Sie ZebraNotes wirklich vollständig entfernen?", "Ja", "Nein", "Ja")]
    [InlineData("ZebraNotes", "Do you really want to completely remove ZebraNotes?", "Yes", "No", "Yes")]
    [InlineData("Gemeinsam genutzte Datei", "Soll die gemeinsam genutzte Datei zebra.dll entfernt werden?", "Ja", "Nein", "Nein")]
    public void Answers_unseen_yes_no_questions(string title, string text, string a, string b, string expected) =>
        Assert.Equal(expected, Clicked(W(title, text, B(a), B(b))));

    [Fact]
    public void Clicks_through_an_unseen_wizard()
    {
        Assert.Equal("Next >", Clicked(W("ZebraNotes Uninstall", "Welcome. Click Next to continue.", B("Next >"), B("Cancel"))));
        Assert.Equal("Uninstall", Clicked(W("ZebraNotes Uninstall", "ZebraNotes will be removed from your computer.", B("< Back"), B("Uninstall"), B("Cancel"))));
        Assert.Equal("Finish", Clicked(W("ZebraNotes", "ZebraNotes was removed successfully.", B("Finish"))));
    }

    [Fact]
    public void Waits_on_progress_window()
    {
        var d = Trained.Value.Decide(W("ZebraNotes", "Please wait while ZebraNotes is being uninstalled.", B("Cancel")));
        Assert.Equal(AssistAction.Wait, d.Action);
    }

    [Fact]
    public void Never_confirms_a_restart()
    {
        var w = new DialogSnapshot("ZebraNotes", new[] { "A restart is required." },
            new[] { new DialogControl(ControlKind.Radio, "Restart now", true), new DialogControl(ControlKind.Radio, "Restart later"), B("Finish") });
        var d = Trained.Value.Decide(w);
        Assert.False(d.Action == AssistAction.Click && w.Controls[d.Index].Label == "Finish");
        Assert.NotEqual("Restart now", Clicked(W("ZebraNotes", "You must restart your computer. Restart now?", B("Restart now"), B("Later"))));
    }

    [Fact]
    public void Never_ticks_an_offer_or_clicks_repair()
    {
        var offer = new DialogSnapshot("ZebraNotes", new[] { "Try our partner toolbar" },
            new[] { new DialogControl(ControlKind.CheckBox, "Install partner toolbar"), B("Next") });
        var d = Trained.Value.Decide(offer);
        Assert.False(d.Action == AssistAction.Click && d.Index == 0);

        var only = W("ZebraNotes", "Select an option", B("Repair"));
        Assert.NotEqual(AssistAction.Click, Trained.Value.Decide(only).Action);
    }

    [Fact]
    public void Error_messages_are_left_to_the_user()
    {
        var d = Trained.Value.Decide(W("ZebraNotes", "Error 1603: A fatal error occurred.", B("OK")));
        Assert.Equal(AssistAction.Ask, d.Action);
    }

    [Fact]
    public void User_corrections_change_the_decision()
    {
        var odd = W("Qx", "Zorblat flimmer?", B("Plonk"), B("Wibble"));
        var m = new DialogModel(Lexicon.FromJson(DialogModel.ReadResource("lexicon.json")));
        var seed = DialogModel.ParseSamples(DialogModel.ReadResource("seed-dialogs.json"));
        var correction = new TrainingSample(odd, 1, 3.0);
        m.Train(seed.Append(correction).Append(correction).Append(correction));
        var d = m.Predict(odd);
        Assert.Equal(1, d.Index);
    }

    [Fact]
    public void Snapshot_json_roundtrip()
    {
        var w = new DialogSnapshot("T „ü“", new[] { "a", "b" }, new[] { B("Ja"), new DialogControl(ControlKind.CheckBox, "x", true) });
        var json = DialogModel.SnapshotToJson(w, 1);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var back = DialogModel.SnapshotFromJson(doc.RootElement);
        Assert.Equal(w.Signature(), back.Signature());
        Assert.Equal(1, doc.RootElement.GetProperty("target").GetInt32());
    }
}

public class OfficeAndSharedSizeTests
{
    const string DeDe = "\"C:\\Program Files\\Common Files\\Microsoft Shared\\ClickToRun\\OfficeClickToRun.exe\" scenario=install scenariosubtype=ARP sourcetype=None productstoremove=O365ProPlusRetail.16_de-de_x-none culture=de-de version.16=16.0";

    [Fact]
    public void Recognises_click_to_run_entries()
    {
        Assert.True(OfficeC2R.IsC2R(DeDe));
        Assert.False(OfficeC2R.IsC2R("MsiExec.exe /X{12345678-1234-1234-1234-123456789012}"));
        Assert.Equal("O365ProPlusRetail.16_de-de_x-none", OfficeC2R.ProductToRemove(DeDe));
        Assert.Equal("O365ProPlusRetail", OfficeC2R.ProductId(DeDe));
    }

    [Fact]
    public void Silent_command_adds_display_level_once()
    {
        var s = OfficeC2R.SilentCommand(DeDe);
        Assert.EndsWith("DisplayLevel=False", s);
        Assert.Equal(s, OfficeC2R.SilentCommand(s));
        Assert.Contains("DisplayLevel=False", OfficeC2R.SilentCommand(DeDe + " DisplayLevel=True"));
        Assert.DoesNotContain("DisplayLevel=True", OfficeC2R.SilentCommand(DeDe + " DisplayLevel=True"));
    }

    [Fact]
    public void Shared_install_folder_is_counted_once()
    {
        var items = new List<ProgramItem>
        {
            new() { Name = "Microsoft 365 - en-us", InstallLocation = @"C:\Program Files\Microsoft Office", SizeBytes = 6_200_000_000 },
            new() { Name = "Microsoft 365 - de-de", InstallLocation = @"C:\Program Files\Microsoft Office\", SizeBytes = 6_200_000_000 },
            new() { Name = "Microsoft 365 - fr-fr", InstallLocation = @"C:\Program Files\Microsoft Office", SizeBytes = 6_200_000_000 },
            new() { Name = "7-Zip", InstallLocation = @"C:\Program Files\7-Zip", SizeBytes = 5_000_000 },
        };
        ProgramScanner.MarkSharedLocations(items);
        Assert.Equal(6_200_000_000 + 5_000_000, items.Sum(i => i.SizeBytes));
        Assert.Equal("", items.Single(i => i.Name.EndsWith("de-de")).SharedWith);
        Assert.Equal("geteilt", items.Single(i => i.Name.EndsWith("fr-fr")).SizeText);
        Assert.Equal("", items.Single(i => i.Name == "7-Zip").SharedWith);
    }
}
