using System.Text.Json;
using MuxSwarm.Utils;
using MuxSwarm.Utils.Memory;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Unit coverage for the deep-memory (reflectionAgent) subsystem: config parse + default-safety,
/// the gatherer's reflection parser, the injector's scoring / relevanceFloor / budget cap, and the
/// SelfHeal SKILL proposal parse. Pure logic - no live model, no background loop, no console I/O.
/// </summary>
public class ReflectionMemoryTests
{
    // ---- Config + default-safety ----------------------------------------------------------

    [Fact]
    public void ReflectionConfig_Defaults_AreStandardAndInert()
    {
        var cfg = new ReflectionConfig();
        Assert.Equal("standard", cfg.Mode);
        Assert.False(cfg.IsDeep);
        Assert.Equal(1500, cfg.InjectTokenBudget);
        Assert.Equal(90, cfg.PollIntervalSeconds);
        Assert.Equal("lead", cfg.Scope);
    }

    [Fact]
    public void SwarmConfig_AbsentReflection_ResolvesToInertStandard()
    {
        var swarm = new SwarmConfig();
        var r = swarm.ResolveReflection();
        Assert.NotNull(r);
        Assert.False(r.IsDeep);
    }

    [Fact]
    public void SwarmConfig_TopLevelMemoryMode_OverridesNestedMode()
    {
        var swarm = new SwarmConfig
        {
            ReflectionAgent = new ReflectionConfig { Mode = "standard" },
            MemoryMode = "deep"
        };
        Assert.True(swarm.ResolveReflection().IsDeep);
    }

    [Fact]
    public void ReflectionConfig_ParsesFromSwarmJson()
    {
        const string json = """
        {
          "reflectionAgent": {
            "mode": "deep",
            "injectTokenBudget": 800,
            "pollIntervalSeconds": 30,
            "relevanceFloor": 0.5,
            "scope": "all"
          }
        }
        """;
        var swarm = JsonSerializer.Deserialize<SwarmConfig>(json);
        var r = swarm!.ResolveReflection();
        Assert.True(r.IsDeep);
        Assert.Equal(800, r.InjectTokenBudget);
        Assert.Equal(30, r.PollIntervalSeconds);
        Assert.Equal(0.5, r.RelevanceFloor);
        Assert.Equal("all", r.Scope);
    }

    // ---- Gatherer parse -------------------------------------------------------------------

    [Fact]
    public void Gatherer_Parse_ReadsImportanceRoleContent()
    {
        var text = "0.8|lead|Always verify file EOL before editing\n"
                 + "0.3|shared|User prefers concise answers\n"
                 + "garbage line without pipes\n"
                 + "notanumber|shared|should be skipped";
        var list = ReflectionGatherer.Parse(text);
        Assert.Equal(2, list.Count);
        Assert.Equal(0.8, list[0].Importance, 3);
        Assert.Equal("lead", list[0].Role);
        Assert.Contains("EOL", list[0].Content);
        Assert.Equal("shared", list[1].Role);
    }

    [Fact]
    public void Gatherer_Parse_ClampsImportanceAndDefaultsRole()
    {
        var list = ReflectionGatherer.Parse("9.9||over-importance no role");
        Assert.Single(list);
        Assert.Equal(1.0, list[0].Importance, 3);
        Assert.Equal("shared", list[0].Role);
    }

    [Fact]
    public void Gatherer_Parse_EmptyOrWhitespace_YieldsNothing()
    {
        Assert.Empty(ReflectionGatherer.Parse(""));
        Assert.Empty(ReflectionGatherer.Parse("   \n  \n"));
    }

    // ---- Injector scoring / floor / budget ------------------------------------------------

    [Fact]
    public void Injector_LexicalOverlap_RewardsSharedTokens()
    {
        double hit = ReflectionInjector.LexicalOverlap("build the dotnet project", "dotnet project build steps");
        double miss = ReflectionInjector.LexicalOverlap("build the dotnet project", "unrelated cooking recipe");
        Assert.True(hit > miss);
        Assert.True(hit > 0.0);
        Assert.Equal(0.0, miss, 3);
    }

    [Fact]
    public void Injector_Score_EmptyQuery_FallsBackToImportanceRecency()
    {
        var now = DateTimeOffset.UtcNow;
        var fresh = new Reflection { Content = "x", Importance = 1.0, Timestamp = now };
        var stale = new Reflection { Content = "x", Importance = 1.0, Timestamp = now.AddDays(-60) };
        double s1 = ReflectionInjector.Score(fresh, "", now);
        double s2 = ReflectionInjector.Score(stale, "", now);
        Assert.True(s1 > s2);   // recency decay
        Assert.True(s1 > 0.0);
    }

    [Fact]
    public void Injector_Score_HigherImportance_ScoresHigher()
    {
        var now = DateTimeOffset.UtcNow;
        var hi = new Reflection { Content = "build dotnet", Importance = 1.0, Timestamp = now };
        var lo = new Reflection { Content = "build dotnet", Importance = 0.0, Timestamp = now };
        Assert.True(ReflectionInjector.Score(hi, "build dotnet", now)
                  > ReflectionInjector.Score(lo, "build dotnet", now));
    }

    // ---- Hybrid semantic ranking (g12.x) --------------------------------------------------

    [Fact]
    public void Injector_Score_SemanticMap_BoostsMatchedReflection()
    {
        var now = DateTimeOffset.UtcNow;
        // Two reflections with identical importance/recency and ZERO lexical overlap with the query.
        var a = new Reflection { Id = "aaa", Content = "compilation error in the build", Importance = 0.5, Timestamp = now };
        var b = new Reflection { Id = "bbb", Content = "unrelated note about lunch", Importance = 0.5, Timestamp = now };
        // Semantic oracle says 'a' is highly similar, 'b' is not.
        var semantic = new Dictionary<string, double> { ["aaa"] = 0.9, ["bbb"] = 0.05 };

        double sa = ReflectionInjector.Score(a, "why does the build keep failing", now, semantic);
        double sb = ReflectionInjector.Score(b, "why does the build keep failing", now, semantic);
        Assert.True(sa > sb, $"semantic-matched reflection should outrank ({sa} vs {sb})");
    }

    [Fact]
    public void Injector_Score_SemanticNull_IsPureLexical()
    {
        var now = DateTimeOffset.UtcNow;
        var r = new Reflection { Id = "x", Content = "dotnet build project", Importance = 0.5, Timestamp = now };
        // With no semantic map, the score equals the lexical relevance path (unchanged behavior).
        double withNull = ReflectionInjector.Score(r, "dotnet build", now, null);
        double legacy = ReflectionInjector.Score(r, "dotnet build", now);
        Assert.Equal(legacy, withNull, 6);
        Assert.True(withNull > 0.0);
    }

    [Fact]
    public void Injector_Score_LexicalStillCountsWhenOutsideTopK()
    {
        var now = DateTimeOffset.UtcNow;
        // Semantic ran (non-null map) but this id is NOT in it (outside topK). Exact-identifier
        // lexical overlap must still contribute so rare-token queries never regress to zero.
        var r = new Reflection { Id = "notInMap", Content = "fixed PR #59 merge conflict g12.92", Importance = 0.5, Timestamp = now };
        var semantic = new Dictionary<string, double> { ["someoneElse"] = 0.8 };
        double s = ReflectionInjector.Score(r, "g12.92 merge conflict", now, semantic);
        Assert.True(s > 0.0, "lexical signal must survive when a reflection is outside the semantic topK");
    }

    // ---- SelfHeal SKILL proposal ----------------------------------------------------------

    [Fact]
    public void SelfHeal_Parse_AcceptsSkillProposal()
    {
        var props = SelfHeal.ParseProposals(
            "BRAIN|reflex|do X\nSKILL|nas-venv-copy|copy NAS python apps local before running\nMEMORY|fact|user on windows");
        Assert.Contains(props, p => p.Type == "SKILL" && p.Key == "nas-venv-copy");
        Assert.Contains(props, p => p.Type == "BRAIN");
        Assert.Contains(props, p => p.Type == "MEMORY");
    }

    [Fact]
    public void SelfHeal_Parse_RejectsUnknownType()
    {
        var props = SelfHeal.ParseProposals("BOGUS|key|content");
        Assert.Empty(props);
    }

    // ---- Two-pass dig: DIG parse + heuristic cue detection -------------------------------

    [Fact]
    public void Gatherer_ParseDigs_ExtractsDigTargets()
    {
        var text = "0.7|shared|some reflection\n"
                 + "DIG|the nginx config path under sandbox\n"
                 + "DIG|  CS0246 build error origin  \n"
                 + "not a dig line";
        var digs = ReflectionGatherer.ParseDigs(text);
        Assert.Equal(2, digs.Count);
        Assert.Equal("the nginx config path under sandbox", digs[0]);
        Assert.Equal("CS0246 build error origin", digs[1]);
    }

    [Fact]
    public void Gatherer_ParseDigs_NoneWhenAbsent()
    {
        Assert.Empty(ReflectionGatherer.ParseDigs("0.5|shared|just a reflection\nanother line"));
    }

    [Theory]
    [InlineData(@"where is that muxswarm config again")]
    [InlineData(@"check C:\Users\jnots\AppData\Local\Mux-Swarm\Configs\Swarm.json")]
    [InlineData(@"the error was in ReflectionGatherer.cs")]
    [InlineData(@"I got a CS0246 exception during build")]
    [InlineData(@"can you locate the handoff doc")]
    [InlineData(@"\\banknas\Public\Jb path to the report")]
    public void Gatherer_DetectCue_FiresOnConcreteCues(string msg)
    {
        Assert.NotNull(ReflectionGatherer.DetectCue(msg));
    }

    [Theory]
    [InlineData("thanks, that looks great")]
    [InlineData("yes go ahead")]
    [InlineData("")]
    [InlineData("ok")]
    public void Gatherer_DetectCue_QuietOnSmallTalk(string msg)
    {
        Assert.Null(ReflectionGatherer.DetectCue(msg));
    }

    // ---- Single-file store: dedup + prune + delta injection ------------------------------

    [Fact]
    public void Store_Append_DedupsByContent()
    {
        var r1 = new Reflection { Content = "the config lives at C:/x/Swarm.json", Importance = 0.5 };
        var r2 = new Reflection { Content = "the config lives at C:/x/Swarm.json", Importance = 0.9 };
        int before = ReflectionStore.LoadAll().Count(r => r.Content == r1.Content);
        ReflectionStore.Append(r1);
        ReflectionStore.Append(r2);
        var matches = ReflectionStore.LoadAll().Where(r => r.Content == r1.Content).ToList();
        Assert.Single(matches);                       // deduped to one
        Assert.Equal(0.9, matches[0].Importance, 3);  // importance upgraded to the max
        // cleanup
        // (left in store; harness ContextDirectory is the test bin - dedup keeps it bounded)
    }

    [Fact]
    public void Injector_BuildDelta_OnlyReturnsNewSinceLastInjection()
    {
        var prior = App.SwarmConfig;
        try
        {
            App.SwarmConfig = new SwarmConfig { MemoryMode = "deep" };
            ReflectionInjector.ResetSession();
            ReflectionInjector.CurrentQuery = "alpha beta gamma marker";
            var uniq = "deltamarker" + System.Guid.NewGuid().ToString("N")[..6];
            ReflectionStore.Append(new Reflection
            {
                Content = $"alpha beta gamma marker {uniq} concrete detail here",
                Role = "lead", Importance = 0.9, Timestamp = System.DateTimeOffset.UtcNow
            });

            // First delta: includes our new reflection.
            var first = ReflectionInjector.BuildDelta("MuxAgent", isLead: true);
            Assert.Contains(uniq, first);

            // Second delta with no new reflections: empty (already injected).
            var second = ReflectionInjector.BuildDelta("MuxAgent", isLead: true);
            Assert.DoesNotContain(uniq, second);
        }
        finally { App.SwarmConfig = prior; ReflectionInjector.ResetSession(); }
    }

    [Fact]
    public void Injector_BuildDelta_SubAgentGetsNothing()
    {
        var prior = App.SwarmConfig;
        try
        {
            App.SwarmConfig = new SwarmConfig { MemoryMode = "deep" };
            ReflectionInjector.ResetSession();
            // isLead:false -> delta is lead/orchestrator only.
            Assert.Equal(string.Empty, ReflectionInjector.BuildDelta("WebAgent", isLead: false));
        }
        finally { App.SwarmConfig = prior; }
    }

    [Fact]
    public void Injector_BuildDelta_InertInStandardMode()
    {
        var prior = App.SwarmConfig;
        try
        {
            App.SwarmConfig = new SwarmConfig(); // mode defaults to standard
            ReflectionInjector.ResetSession();
            Assert.Equal(string.Empty, ReflectionInjector.BuildDelta("MuxAgent", isLead: true));
        }
        finally { App.SwarmConfig = prior; }
    }

    // ---- New configurable tunables (relevanceFloor surfaced + store/dig caps) -------------

    [Fact]
    public void ReflectionConfig_NewTunables_ParseFromSwarmJson()
    {
        const string json = """
        {
          "reflectionAgent": {
            "mode": "deep",
            "maxReflections": 250,
            "historyWindow": 12,
            "maxDigsPerTick": 3,
            "digMaxFilesScanned": 800,
            "digMaxMatches": 15,
            "digMaxReadChars": 4000
          }
        }
        """;
        var r = JsonSerializer.Deserialize<SwarmConfig>(json)!.ResolveReflection();
        Assert.Equal(250, r.MaxReflections);
        Assert.Equal(12, r.HistoryWindow);
        Assert.Equal(3, r.MaxDigsPerTick);
        Assert.Equal(800, r.DigMaxFilesScanned);
        Assert.Equal(15, r.DigMaxMatches);
        Assert.Equal(4000, r.DigMaxReadChars);
    }

    [Fact]
    public void ReflectionConfig_NewTunables_HaveSaneDefaults()
    {
        var r = new ReflectionConfig();
        Assert.Equal(30000, r.MaxReflections);
        Assert.Equal(4000, r.InjectQueryTimeoutMs);
        Assert.Equal(30, r.HistoryWindow);
        Assert.Equal(2, r.MaxDigsPerTick);
        Assert.Equal(4000, r.DigMaxFilesScanned);
        Assert.Equal(40, r.DigMaxMatches);
        Assert.Equal(8000, r.DigMaxReadChars);
    }
}
