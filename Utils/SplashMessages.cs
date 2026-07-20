using System;

namespace MuxSwarm.Utils;

/// <summary>
/// Curated, fully-embedded splash-line pool. Every startup picks ONE line at random so the banner
/// feels unique run to run, with zero network dependency (instant + offline-safe: a network
/// quote-of-the-day would add latency and a failure mode to every launch). The pool spans real,
/// attribution-checked engineering/science quotes, verified dev/CS/science facts, mux-swarm power
/// tips, and short engagement nudges. Content sources: reports/RESEARCH_splash_quotes_verified_*
/// and reports/SPLASH_TIPS_devfacts_* (WebAgent, 2026-07-16; misattributions corrected, apocrypha
/// dropped). Replaces the former static "CLI-native agentic swarm OS" tagline (kept as one classic
/// entry so it still surfaces occasionally).
/// </summary>
internal static class SplashMessages
{
    private static readonly Random _rng = new();

    // A few classic taglines, including the original, so the brand line still appears sometimes.
    private static readonly string[] Taglines =
    {
        "CLI-native agentic swarm OS",
        "Orchestrate agents. Ship faster.",
        "One terminal. A whole swarm.",
    };

    private static readonly string[] Quotes =
    {
        "\"Simplicity is prerequisite for reliability.\" - Edsger W. Dijkstra",
        "\"Program testing can be used to show the presence of bugs, but never to show their absence.\" - Edsger W. Dijkstra",
        "\"Elegance is not a dispensable luxury but a quality that decides between success and failure.\" - Edsger W. Dijkstra",
        "\"The competent programmer is fully aware of the strictly limited size of his own skull.\" - Edsger W. Dijkstra",
        "\"Premature optimization is the root of all evil.\" - Donald Knuth",
        "\"Beware of bugs in the above code; I have only proved it correct, not tried it.\" - Donald Knuth",
        "\"Science is what we understand well enough to explain to a computer. Art is everything else we do.\" - Donald Knuth",
        "\"What I cannot create, I do not understand.\" - Richard Feynman",
        "\"The first principle is that you must not fool yourself, and you are the easiest person to fool.\" - Richard Feynman",
        "\"For a successful technology, reality must take precedence over public relations, for nature cannot be fooled.\" - Richard Feynman",
        "\"Talk is cheap. Show me the code.\" - Linus Torvalds",
        "\"The best way to predict the future is to invent it.\" - Alan Kay",
        "\"Simple things should be simple, complex things should be possible.\" - Alan Kay",
        "\"It's easier to ask forgiveness than it is to get permission.\" - Grace Hopper",
        "\"One accurate measurement is worth a thousand expert opinions.\" - Grace Hopper",
        "\"A ship in harbor is safe, but that is not what ships are built for.\" - John A. Shedd",
        "\"Perfection is achieved, not when there is nothing more to add, but when there is nothing left to take away.\" - Antoine de Saint-Exupery",
        "\"One of my most productive days was throwing away 1000 lines of code.\" - Ken Thompson",
        "\"When in doubt, use brute force.\" - Ken Thompson",
        "\"Focus is a matter of deciding what things you're not going to do.\" - John Carmack",
        "\"Everyone knows that debugging is twice as hard as writing a program in the first place.\" - Brian Kernighan",
        "\"Controlling complexity is the essence of computer programming.\" - Brian Kernighan",
        "\"The only way to learn a new programming language is by writing programs in it.\" - Brian Kernighan and Dennis Ritchie",
        "\"Given enough eyeballs, all bugs are shallow.\" - Eric S. Raymond",
        "\"Programs must be written for people to read, and only incidentally for machines to execute.\" - Harold Abelson and Gerald Jay Sussman",
        "\"Simplicity does not precede complexity, but follows it.\" - Alan Perlis",
        "\"Fools ignore complexity. Pragmatists suffer it. Some can avoid it. Geniuses remove it.\" - Alan Perlis",
        "\"A language that doesn't affect the way you think about programming is not worth knowing.\" - Alan Perlis",
        "\"A complex system that works is invariably found to have evolved from a simple system that worked.\" - John Gall",
        "\"Make it work, make it right, make it fast.\" - Kent Beck",
        "\"I'm not a great programmer; I'm just a good programmer with great habits.\" - Kent Beck",
        "\"Any fool can write code that a computer can understand. Good programmers write code that humans can understand.\" - Martin Fowler",
        "\"Duplication is far cheaper than the wrong abstraction.\" - Sandi Metz",
        "\"There are only two hard things in computer science: cache invalidation and naming things.\" - Phil Karlton",
        "\"All problems in computer science can be solved by another level of indirection.\" - David Wheeler",
        "\"The purpose of computing is insight, not numbers.\" - Richard Hamming",
        "\"If you don't work on important problems, it's not likely that you'll do important work.\" - Richard Hamming",
        "\"Chance favors only the prepared mind.\" - Louis Pasteur",
        "\"If I have seen further it is by standing on the shoulders of giants.\" - Isaac Newton",
        "\"One never notices what has been done; one can only see what remains to be done.\" - Marie Curie",
        "\"I have no special talents. I am only passionately curious.\" - Albert Einstein",
        "\"The important thing is not to stop questioning.\" - Albert Einstein",
        "\"We can only see a short distance ahead, but we can see plenty there that needs to be done.\" - Alan Turing",
        "\"The Analytical Engine weaves algebraical patterns just as the Jacquard-loom weaves flowers and leaves.\" - Ada Lovelace",
        "\"The present is theirs; the future, for which I really worked, is mine.\" - Nikola Tesla",
        "\"Understanding is a kind of ecstasy.\" - Carl Sagan",
        "\"If people do not believe that mathematics is simple, it is only because they do not realize how complicated life is.\" - John von Neumann",
        "\"Modularity based on abstraction is the way things get done.\" - Barbara Liskov",
        "\"There are only two kinds of languages: the ones people complain about and the ones nobody uses.\" - Bjarne Stroustrup",
        "\"Code is read much more often than it is written.\" - Guido van Rossum",
        "\"Simple is better than complex.\" - Tim Peters",
        "\"Hofstadter's Law: It always takes longer than you expect, even when you take into account Hofstadter's Law.\" - Douglas Hofstadter",
        "\"The first 90% of the code accounts for the first 90% of the development time. The remaining 10% accounts for the other 90%.\" - Tom Cargill",
        "\"Data dominates. If you've chosen the right data structures and organized things well, the algorithms will almost always be self-evident.\" - Rob Pike",
        "\"The programmer, like the poet, works only slightly removed from pure thought-stuff.\" - Fred Brooks",
        "\"Adding manpower to a late software project makes it later.\" - Fred Brooks",
        "\"The cheapest, fastest, and most reliable components are those that aren't there.\" - Gordon Bell",
        "\"Good design is as little design as possible.\" - Dieter Rams",
        "\"The details are not the details. They make the design.\" - Charles Eames",
        "\"Everyone designs who devises courses of action aimed at changing existing situations into preferred ones.\" - Herbert Simon",
        "\"Creators need an immediate connection to what they create.\" - Bret Victor",
        "\"Real artists ship.\" - Steve Jobs",
        "\"Everybody in this country should learn to program a computer, because it teaches you how to think.\" - Steve Jobs",
        "\"The best is the enemy of the good.\" - Voltaire",
        "\"A poem is never finished, only abandoned.\" - Paul Valery",
        "\"Ever tried. Ever failed. No matter. Try again. Fail again. Fail better.\" - Samuel Beckett",
        "\"We are what we repeatedly do. Excellence, then, is not an act, but a habit.\" - Will Durant",
        "\"No great thing is created suddenly.\" - Epictetus",
        "\"Waste no more time arguing what a good man should be. Be one.\" - Marcus Aurelius",
        "\"If there is no struggle, there is no progress.\" - Frederick Douglass",
        "\"Inspiration is for amateurs; the rest of us just show up and get to work.\" - Chuck Close",
        "\"How we spend our days is, of course, how we spend our lives.\" - Annie Dillard",
        "\"Clarity about what matters provides clarity about what does not.\" - Cal Newport",
        "\"Problems worthy of attack prove their worth by fighting back.\" - Piet Hein",
        "\"If you can't solve a problem, then there is an easier problem you can solve: find it.\" - George Polya",
        "\"Iron rusts from disuse; stagnant water loses its purity; even so does inaction sap the vigor of the mind.\" - Leonardo da Vinci",
        "\"There is nothing so useless as doing efficiently that which should not be done at all.\" - Peter Drucker",
        "\"Remember, always, that everything you know, and everything everyone knows, is only a model.\" - Donella Meadows",
        "\"A system is never the sum of its parts; it's the product of their interactions.\" - Russell Ackoff",
    };

    private static readonly string[] Facts =
    {
        "In 1947 Harvard Mark II engineers taped a real moth into the logbook as the \"first actual case of bug being found\"; the term itself predates it.",
        "Grace Hopper built A-0, the first compiler, in 1952; her FLOW-MATIC language directly shaped COBOL.",
        "The first ARPANET message (Oct 29, 1969) was meant to be \"LOGIN\" but the system crashed after just \"LO\".",
        "Ada Lovelace published the first algorithm intended for a machine in 1843, for Babbage's unbuilt Analytical Engine.",
        "The Apollo Guidance Computer landed humans on the Moon with about 4KB of RAM; Margaret Hamilton led its software team.",
        "Ken Thompson wrote the first version of UNIX in about three weeks in 1969, on a spare PDP-7 at Bell Labs.",
        "Dennis Ritchie created C in 1972 largely to rewrite UNIX, making it one of the first OSes in a high-level language.",
        "The world's first webcam watched a coffee pot at Cambridge so researchers could skip trips to an empty pot; it went online in 1993.",
        "The first registered .com domain was symbolics.com, on March 15, 1985.",
        "\"Hello, World\" traces to Brian Kernighan's 1972 tutorial for the B language, later canonized in the 1978 C book.",
        "Brain, the first PC virus (1986), was written by two brothers in Lahore and included their names, address, and phone number.",
        "Linus Torvalds announced Linux in 1991 as \"just a hobby, won't be big and professional like gnu\".",
        "Ray Tomlinson picked the @ sign in 1971 to separate user from host in the first network email addresses.",
        "Python is named after Monty Python's Flying Circus, not the snake.",
        "Linus Torvalds wrote the first working version of Git in about ten days in April 2005, after BitKeeper access was revoked.",
        "\"Byte\" was coined by Werner Buchholz at IBM in 1956, deliberately misspelled from \"bite\" so it would not be confused with \"bit\".",
        "IBM's 3380 disk (1980) stored 2.5GB, weighed around 550 pounds, and cost roughly $100,000.",
        "The 1988 Morris worm, written by a Cornell grad student, knocked out about 10% of the roughly 60,000 machines then online.",
        "A teaspoon of neutron star material would weigh billions of tons on Earth.",
        "Sealed honey found in 3,000-year-old Egyptian tombs was still edible; low water and high acidity make it nearly immortal.",
        "There are more possible chess games (about 10^120) than atoms in the observable universe (about 10^80).",
        "Octopuses have three hearts and blue blood, based on copper instead of iron.",
        "A day on Venus outlasts its year: 243 Earth days to rotate, about 225 to orbit the Sun.",
        "A photon takes thousands of years to random-walk out of the Sun's core, then just 8 minutes to reach Earth.",
        "Sharks are older than trees: sharks appeared around 450 million years ago, the first trees about 385 million.",
        "Bananas are mildly radioactive from potassium-40; the \"banana equivalent dose\" is a real informal radiation unit.",
        "Tardigrades survived direct exposure to the vacuum of space in a 2007 orbital experiment.",
        "Most atoms in your body were forged inside stars; the hydrogen dates back to the Big Bang.",
        "Voyager 1, launched 1977, is over 15 billion miles away and still phones home with a transmitter weaker than a refrigerator bulb.",
        "GPS satellites correct for relativity: their clocks gain ~38 microseconds daily, which uncorrected would drift fixes by ~10 km per day.",
        "The Eiffel Tower grows about 15 centimeters taller in summer from thermal expansion.",
        "Wombats produce cube-shaped droppings, formed by varying elasticity in their intestines.",
        "Jupiter's Great Red Spot is a storm that has been observed continuously since at least the 1830s.",
        "Parkinson's Law: work expands to fill the time available for its completion (Cyril Northcote Parkinson, 1955).",
        "The Pomodoro Technique is named after Francesco Cirillo's tomato-shaped kitchen timer: 25 minutes of focus, then a short break.",
        "The two-minute rule (David Allen): if it takes under two minutes, do it now instead of tracking it.",
        "Deep work (Cal Newport): schedule blocks of undistracted focus; shallow work fills whatever time you leave unguarded.",
        "The Zeigarnik effect: unfinished tasks keep nagging your mind; writing them down releases the loop.",
        "Research by Gloria Mark found it takes about 23 minutes to fully refocus after an interruption. Guard your flow.",
        "Eat the frog: do your most dreaded task first; everything after it feels easier.",
        "Hofstadter's Law: it always takes longer than you expect, even when you take Hofstadter's Law into account.",
        "The planning fallacy (Kahneman and Tversky): we underestimate task time even when we know our past estimates were wrong.",
        "Don't break the chain: a visible streak of daily practice is one of the simplest habit engines ever devised.",
        "Attention residue (Sophie Leroy): part of your mind stays on the last task after switching; finish or park work cleanly.",
        "Make good habits obvious and easy, bad ones invisible and hard (James Clear).",
        "Skill grows at the edge of ability: deliberate practice means working just beyond what is comfortable (Anders Ericsson).",
        "Conway's Law: organizations ship systems that mirror their own communication structures (Melvin Conway, 1968).",
        "Postel's Law: be conservative in what you send, liberal in what you accept (Jon Postel, TCP specification).",
        "Gall's Law: a complex system that works invariably evolved from a simple system that worked.",
        "YAGNI: you aren't gonna need it. Build for the requirement you have, not the one you imagine.",
        "The UNIX philosophy: write programs that do one thing well and work together, with text streams as the universal interface.",
        "Premature optimization is the root of all evil, in about 97% of cases (Donald Knuth, 1974).",
        "Brooks's Law: adding people to a late software project makes it later (The Mythical Man-Month, 1975).",
        "Kernighan's Law: debugging is twice as hard as writing code, so code written at your cleverness limit is undebuggable.",
        "Chesterton's Fence: never remove something until you understand why it was put there.",
        "Hyrum's Law: with enough users, every observable behavior of your API will be depended on by somebody.",
        "The Law of Leaky Abstractions: all non-trivial abstractions leak, so learn what lives underneath yours (Joel Spolsky).",
        "The Boy Scout rule: leave the code a little cleaner than you found it.",
        "The ninety-ninety rule: the first 90% of the code takes 90% of the time; the remaining 10% takes the other 90% (Tom Cargill).",
        "Wirth's Law: software gets slower faster than hardware gets faster.",
    };

    private static readonly string[] Tips =
    {
        "Tip: /pswarm fans independent work to parallel agents; /swarm runs a dependency-aware loop.",
        "Tip: start long shell jobs with execute_command_async, then wait_job_progress; never sleep-poll blindly.",
        "Tip: wait_job_progress auto-continues from where you last read, so you never re-scan old output.",
        "Tip: /agent for a single specialist, /stateless for a one-off task with no session memory.",
        "Tip: /onboard once to set your operator profile; every agent inherits it.",
        "Tip: press Esc (or q) to cancel the current turn without killing your session.",
        "Tip: /qc or /qm exits an active session; /status shows your current config.",
        "Tip: /workflow runs a saved multi-phase DAG, feeding each step's result to the next.",
        "Tip: skills are reusable operational modules; list_skills to see what is loaded.",
        "Tip: the Python REPL worker keeps variables across calls; restart_python_worker clears them.",
        "Tip: delegate mechanical or exploratory work to sub-agents to keep your main context lean.",
        "Tip: /help is the full command reference; /shortcuts lists keyboard bindings.",
        "Did you know? Every agent session is isolated by construction, so parallel children never clash.",
        "Did you know? Mux resolves config relative to its own binary, so it runs from anywhere.",
    };

    private static readonly string[] Nudges =
    {
        "The best time to start was yesterday. The second best time is now.",
        "Small steps, shipped daily, outrun grand plans that never leave the whiteboard.",
        "Momentum compounds. One good commit today is worth ten planned for someday.",
        "You don't have to be great to start, but you have to start to be great.",
        "Progress over perfection: a working draft beats a perfect idea.",
        "The work you avoid is usually the work that matters most. Start there.",
        "Focus is a superpower in a distracted world. Guard it fiercely.",
        "Every expert was once a beginner who refused to quit.",
    };

    /// <summary>
    /// Pick one splash line at random from the whole pool. Returns (label, text): label is a short
    /// dim category prefix for taglines/tips/nudges, or the author for a quote, or empty for a bare
    /// fact. The caller styles label + text. Weighted so quotes and facts (the largest, richest sets)
    /// dominate, with tips, nudges, and the brand tagline sprinkled in.
    /// </summary>
    public static (string Label, string Text) Pick()
    {
        // Weighted bucket selection (out of 100): quotes 40, facts 34, tips 14, nudges 8, tagline 4.
        int roll = _rng.Next(100);
        if (roll < 40)
        {
            // "Quote text." - Author  ->  split into (Author, "Quote text.")
            string q = Quotes[_rng.Next(Quotes.Length)];
            int sep = q.LastIndexOf(" - ", StringComparison.Ordinal);
            if (sep > 0)
                return (q.Substring(sep + 3).Trim(), q.Substring(0, sep + 1).Trim());
            return ("", q);
        }
        if (roll < 74) return ("", Facts[_rng.Next(Facts.Length)]);
        if (roll < 88) return ("", Tips[_rng.Next(Tips.Length)]);
        if (roll < 96) return ("", Nudges[_rng.Next(Nudges.Length)]);
        return ("", Taglines[_rng.Next(Taglines.Length)]);
    }

    /// <summary>Total number of distinct lines in the pool (for tests / diagnostics).</summary>
    public static int PoolSize => Quotes.Length + Facts.Length + Tips.Length + Nudges.Length + Taglines.Length;
}
