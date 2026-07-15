namespace BunnyCompanion.Engine;

public sealed record PetFrame(string Sprite, int DurationMilliseconds);

public sealed record PetActionDefinition(
    string Key,
    IReadOnlyList<PetFrame> Frames,
    bool Loop = false);

public static class PetActionCatalog
{
    public static IReadOnlyDictionary<string, PetActionDefinition> All { get; } = Build();

    public static PetActionDefinition Get(string key) =>
        All.TryGetValue(key, out var action) ? action : All["idle"];

    private static Dictionary<string, PetActionDefinition> Build()
    {
        static PetFrame F(string sprite, int milliseconds) => new(sprite, milliseconds);
        static PetActionDefinition Single(string key, int duration = 1300) =>
            new(key, [F(key, duration)]);

        var actions = new Dictionary<string, PetActionDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["idle"] = new("idle",
                [F("idle", 1200), F("breathe", 420), F("idle", 950), F("blink", 150), F("breathe", 380)],
                Loop: true),
            ["walk"] = new("walk",
                [F("walk_1", 130), F("walk_2", 130), F("walk_3", 130), F("walk_2", 130)],
                Loop: true),
            ["wave"] = new("wave", [F("idle", 120), F("wave", 950), F("wave", 420), F("recover", 280)]),
            ["jump"] = new("jump", [F("jump_ready", 260), F("jump", 520), F("land", 330), F("recover", 300)]),
            ["recover"] = new("recover", [F("recover", 420), F("idle", 250)]),
            ["headpat"] = new("headpat", [F("headpat", 850), F("delighted", 700), F("idle", 250)]),
            ["heart"] = new("heart", [F("wink", 450), F("heart", 1200), F("kiss", 700)]),
            ["kiss"] = new("kiss", [F("wink", 350), F("kiss", 1100), F("shy", 500)]),
            ["clap"] = new("clap", [F("clap", 280), F("delighted", 260), F("clap", 280), F("celebrate", 700)]),
            ["dance"] = new("dance",
                [F("music", 450), F("dance", 430), F("music", 450), F("tiptoe", 420), F("dance", 520)]),
            ["sleep"] = new("sleep",
                [F("sleepy", 550), F("yawn", 650), F("drowsy", 700), F("sleep_curl", 2200), F("sleep_side", 2200)],
                Loop: true),
            ["read"] = new("read", [F("sit", 350), F("read", 4800)]),
            ["focus"] = new("focus", [F("read", 2200), F("drink", 700), F("read", 2200)], Loop: true),
            ["music"] = new("music", [F("music", 1800), F("dance", 750), F("music", 900)]),
            ["dragged"] = new("dragged", [F("dragged", 300)], Loop: true),
            ["birthday"] = new("birthday", [F("gift", 650), F("birthday", 1800), F("celebrate", 900)]),
            ["reminder"] = new("reminder", [F("reminder", 1200), F("point", 1000)]),
            ["rain"] = new("rain", [F("rain", 1800), F("flowers", 900)]),
            ["shy"] = new("shy", [F("shy", 800), F("bashful", 800), F("wink", 450)]),
            ["pout"] = new("pout", [F("pout", 800), F("annoyed", 650), F("curious", 500)]),
            ["comfort"] = new("comfort", [F("sad", 650), F("headpat", 850), F("delighted", 550)]),
            ["curious"] = new("curious", [F("look_back", 500), F("curious", 1050)]),
            ["stretch"] = new("stretch", [F("sleepy", 350), F("yawn", 500), F("stretch", 1050)]),
            ["drink"] = new("drink", [F("point", 500), F("drink", 1300), F("delighted", 450)]),
            ["gift"] = new("gift", [F("shy", 450), F("gift", 1500), F("delighted", 500)]),
            ["plush"] = new("plush", [F("plush", 1700), F("bashful", 500)]),
            ["sit"] = Single("sit", 2200),
            ["kneel"] = Single("kneel", 1800),
            ["laugh"] = Single("laugh", 1400),
            ["surprised"] = Single("surprised", 1150),
            ["flowers"] = Single("flowers", 1700),
            // point 精灵已有；作为指向/说明类动作入口
            ["point"] = new("point", [F("point", 900), F("wave", 500)]),
        };

        return actions;
    }
}
