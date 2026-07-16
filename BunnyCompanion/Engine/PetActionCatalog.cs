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

        // 时长原则：循环动作帧间隔均匀；走路 4 拍闭环；单帧表情略留余量避免「一闪就切」
        var actions = new Dictionary<string, PetActionDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["idle"] = new("idle",
                [F("idle", 1400), F("breathe", 480), F("idle", 1100), F("blink", 180), F("breathe", 420)],
                Loop: true),
            // 走路：四拍闭环 walk1→2→3→2，约 160ms/拍，避免 130ms 过大姿态差造成「抽」
            ["walk"] = new("walk",
                [F("walk_1", 160), F("walk_2", 160), F("walk_3", 160), F("walk_2", 160)],
                Loop: true),
            ["wave"] = new("wave", [F("idle", 140), F("wave", 1000), F("wave", 450), F("recover", 320)]),
            ["jump"] = new("jump", [F("jump_ready", 280), F("jump", 540), F("land", 360), F("recover", 320)]),
            ["recover"] = new("recover", [F("recover", 450), F("idle", 280)]),
            ["headpat"] = new("headpat", [F("headpat", 900), F("delighted", 720), F("idle", 280)]),
            ["heart"] = new("heart", [F("wink", 480), F("heart", 1250), F("kiss", 720)]),
            ["kiss"] = new("kiss", [F("wink", 380), F("kiss", 1150), F("shy", 520)]),
            ["clap"] = new("clap", [F("clap", 300), F("delighted", 280), F("clap", 300), F("celebrate", 720)]),
            ["dance"] = new("dance",
                [F("music", 480), F("dance", 460), F("music", 480), F("tiptoe", 440), F("dance", 540)]),
            ["sleep"] = new("sleep",
                [F("sleepy", 580), F("yawn", 680), F("drowsy", 720), F("sleep_curl", 2400), F("sleep_side", 2400)],
                Loop: true),
            ["read"] = new("read", [F("sit", 380), F("read", 5000)]),
            ["focus"] = new("focus", [F("read", 2400), F("drink", 750), F("read", 2400)], Loop: true),
            ["music"] = new("music", [F("music", 1900), F("dance", 800), F("music", 950)]),
            ["dragged"] = new("dragged", [F("dragged", 400)], Loop: true),
            ["birthday"] = new("birthday", [F("gift", 680), F("birthday", 1900), F("celebrate", 950)]),
            ["reminder"] = new("reminder", [F("reminder", 1250), F("point", 1050)]),
            ["rain"] = new("rain", [F("rain", 1900), F("flowers", 950)]),
            ["shy"] = new("shy", [F("shy", 850), F("bashful", 850), F("wink", 480)]),
            ["pout"] = new("pout", [F("pout", 850), F("annoyed", 680), F("curious", 520)]),
            ["comfort"] = new("comfort", [F("sad", 680), F("headpat", 900), F("delighted", 580)]),
            ["curious"] = new("curious", [F("look_back", 540), F("curious", 1100)]),
            ["stretch"] = new("stretch", [F("sleepy", 380), F("yawn", 520), F("stretch", 1100)]),
            ["drink"] = new("drink", [F("point", 520), F("drink", 1350), F("delighted", 480)]),
            ["gift"] = new("gift", [F("shy", 480), F("gift", 1550), F("delighted", 520)]),
            ["plush"] = new("plush", [F("plush", 1750), F("bashful", 520)]),
            ["sit"] = Single("sit", 2300),
            ["kneel"] = Single("kneel", 1900),
            ["laugh"] = Single("laugh", 1450),
            ["surprised"] = Single("surprised", 1200),
            ["flowers"] = Single("flowers", 1750),
            ["point"] = new("point", [F("point", 950), F("wave", 520)]),
            ["delighted"] = new("delighted", [F("delighted", 950), F("clap", 520)]),
            ["wink"] = new("wink", [F("wink", 750), F("shy", 520)]),
            ["bashful"] = new("bashful", [F("bashful", 850), F("shy", 520)]),
            ["look_back"] = new("look_back", [F("look_back", 750), F("curious", 620)]),
            ["tiptoe"] = new("tiptoe", [F("tiptoe", 950), F("recover", 380)]),
            ["land"] = new("land", [F("land", 540), F("recover", 420)]),
            ["annoyed"] = new("annoyed", [F("annoyed", 850), F("pout", 520)]),
            ["sleepy"] = new("sleepy", [F("sleepy", 750), F("yawn", 620)]),
            ["celebrate"] = new("celebrate", [F("celebrate", 950), F("clap", 520)]),
            ["dizzy_spin"] = new("dizzy_spin", [F("surprised", 580), F("pout", 480), F("recover", 420)]),
        };

        return actions;
    }
}
