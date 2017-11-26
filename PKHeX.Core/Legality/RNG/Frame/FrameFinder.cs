﻿using System.Collections.Generic;

namespace PKHeX.Core
{
    public static class FrameFinder
    {
        /// <summary>
        /// Checks a <see cref="PIDIV"/> to see if any encounter frames can generate the spread. Requires further filtering against matched Encounter Slots and generation patterns.
        /// </summary>
        /// <param name="pidiv">Matched <see cref="PIDIV"/> containing <see cref="PIDIV.RNG"/> info and <see cref="PIDIV.OriginSeed"/>.</param>
        /// <param name="pk"><see cref="PKM"/> object containing various accessible information required for the encounter.</param>
        /// <returns><see cref="IEnumerable{Frame}"/> to yield possible encounter details for further filtering</returns>
        public static IEnumerable<Frame> GetFrames(PIDIV pidiv, PKM pk)
        {
            if (pidiv.RNG == null)
                yield break;
            FrameGenerator info = new FrameGenerator(pidiv, pk);
            if (info.FrameType == FrameType.None)
                yield break;

            info.Nature = pk.EncryptionConstant % 25;

            // gather possible nature determination seeds until a same-nature PID breaks the unrolling
            IEnumerable<SeedInfo> seeds = SeedInfo.GetSeedsUntilNature(pidiv, info);

            var frames = pidiv.Type == PIDType.CuteCharm 
                ? FilterCuteCharm(seeds, pidiv, info) 
                : FilterNatureSync(seeds, pidiv, info);

            var refined = RefineFrames(frames, info);
            foreach (var z in refined)
                yield return z;
        }

        private static IEnumerable<Frame> RefineFrames(IEnumerable<Frame> frames, FrameGenerator info)
        {
            return info.FrameType == FrameType.MethodH
                ? RefineFrames3(frames, info)
                : RefineFrames4(frames, info);
        }

        private static IEnumerable<Frame> RefineFrames3(IEnumerable<Frame> frames, FrameGenerator info)
        {
            // ESV
            // Level
            // Nature
            // Current Seed of the frame is the Level Calc (frame before nature)
            var list = new List<Frame>();
            foreach (var f in frames)
            {
                // Current Seed of the frame is the Level Calc
                var prev = info.RNG.Prev(f.Seed); // ESV
                var rand = prev >> 16;
                {
                    f.ESV = rand;
                    yield return f;
                }

                if (f.Lead != LeadRequired.None || !info.AllowLeads) // Emerald
                    continue;

                // Generate frames for other slots after the regular slots
                list.Add(f);
            }

            // Check leads -- none in list if leads are not allowed
            // Certain leads inject a RNG call
            // 3 different rand places
            foreach (var f in list)
            {
                LeadRequired lead;
                var prev0 = f.Seed; // 0
                var prev1 = info.RNG.Prev(f.Seed); // -1 
                var prev2 = info.RNG.Prev(prev1); // -2

                // Modify Call values 
                var p0 = prev0 >> 16;
                var p1 = prev1 >> 16;
                var p2 = prev2 >> 16;

                // Pressure, Hustle, Vital Spirit = Force Maximum Level from slot
                // -2 ESV
                // -1 Level
                //  0 LevelMax proc (Random() & 1)
                //  1 Nature
                bool max = p0 % 2 == 1;
                lead = max ? LeadRequired.PressureHustleSpirit : LeadRequired.PressureHustleSpiritFail;
                yield return info.GetFrame(prev2, lead, p2);

                // Keen Eye, Intimidate
                // -2 ESV
                // -1 Level
                //  0 Level Adequate Check !(Random() % 2 == 1) rejects --  rand%2==1 is adequate
                //  1 Nature
                // Note: if this check fails, the encounter generation routine is aborted.
                if (max) // same result as above, no need to recalculate
                {
                    lead = LeadRequired.IntimidateKeenEye;
                    yield return info.GetFrame(prev2, lead, p2);
                }

                // Cute Charm
                // -2 ESV
                // -1 CC Proc (Random() % 3 != 0)
                //  0 Level
                //  1 Nature
                bool cc = p1 % 3 != 0;
                lead = cc ? LeadRequired.CuteCharm : LeadRequired.CuteCharmFail;
                yield return info.GetFrame(prev2, lead, p2);

                // Static or Magnet Pull
                // -2 SlotProc (Random % 2 == 0)
                // -1 ESV (select slot)
                //  0 Level
                //  1 Nature
                bool force = p2 % 2 == 0;
                if (force)
                {
                    // Since a failed proc is indistinguishable from the default frame calls, only generate if it succeeds.
                    lead = LeadRequired.StaticMagnet;
                    yield return info.GetFrame(prev2, lead, p1);
                }
            }
        }
        private static IEnumerable<Frame> RefineFrames4(IEnumerable<Frame> frames, FrameGenerator info)
        {
            var list = new List<Frame>();
            foreach (var f in frames)
            {
                // Current Seed of the frame is the ESV.
                var rand = f.Seed >> 16;
                {
                    f.ESV = rand;
                    yield return f;
                }

                if (f.Lead != LeadRequired.None)
                    continue;

                // Generate frames for other slots after the regular slots
                list.Add(f);
            }
            foreach (var f in list)
            {
                // Level Modifiers between ESV and Nature
                var prev = info.RNG.Prev(f.Seed);
                var p16 = prev >> 16;

                yield return info.GetFrame(prev, LeadRequired.IntimidateKeenEye, p16);
                yield return info.GetFrame(prev, LeadRequired.PressureHustleSpirit, p16);

                // Slot Modifiers before ESV
                var force = (info.DPPt ? p16 >> 15 : p16 & 1) == 1;
                if (!force)
                    continue;

                var rand = f.Seed >> 16;
                yield return info.GetFrame(prev, LeadRequired.StaticMagnet, rand);
            }
        }

        /// <summary>
        /// Filters the input <see cref="SeedInfo"/> according to a Nature Lock frame generation pattern.
        /// </summary>
        /// <param name="seeds">Seed Information for the frame</param>
        /// <param name="pidiv">PIDIV Info for the frame</param>
        /// <param name="info">Search Info for the frame</param>
        /// <returns>Possible matches to the Nature Lock frame generation pattern</returns>
        private static IEnumerable<Frame> FilterNatureSync(IEnumerable<SeedInfo> seeds, PIDIV pidiv, FrameGenerator info)
        {
            foreach (var seed in seeds)
            {
                var s = seed.Seed;
                var rand = s >> 16;
                bool sync = info.AllowLeads && !seed.Charm3 && (info.DPPt ? rand >> 15 : rand & 1) == 0;
                bool reg = (info.DPPt ? rand / 0xA3E : rand % 25) == info.Nature;
                if (!sync && !reg) // doesn't generate nature frame
                    continue;

                uint prev = pidiv.RNG.Prev(s);
                if (info.AllowLeads && reg) // check for failed sync
                {
                    var failsync = (info.DPPt ? prev >> 31 : (prev >> 16) & 1) != 1;
                    if (failsync)
                        yield return info.GetFrame(pidiv.RNG.Prev(prev), LeadRequired.SynchronizeFail);
                }
                if (sync)
                    yield return info.GetFrame(prev, LeadRequired.Synchronize);
                if (reg)
                {
                    if (seed.Charm3)
                        yield return info.GetFrame(prev, LeadRequired.CuteCharm);
                    else
                        yield return info.GetFrame(prev, LeadRequired.None);
                }
            }
        }

        /// <summary>
        /// Filters the input <see cref="SeedInfo"/> according to a Cute Charm frame generation pattern.
        /// </summary>
        /// <param name="seeds">Seed Information for the frame</param>
        /// <param name="pidiv">PIDIV Info for the frame</param>
        /// <param name="info">Search Info for the frame</param>
        /// <returns>Possible matches to the Cute Charm frame generation pattern</returns>
        private static IEnumerable<Frame> FilterCuteCharm(IEnumerable<SeedInfo> seeds, PIDIV pidiv, FrameGenerator info)
        {
            foreach (var seed in seeds)
            {
                var s = seed.Seed;

                var rand = s >> 16;
                var nature = info.DPPt ? rand / 0xA3E : rand % 25;
                if (nature != info.Nature)
                    continue;

                var prev = pidiv.RNG.Prev(s);
                var proc = prev >> 16;
                bool charmProc = (info.DPPt ? proc / 0x5556 : proc % 3) != 0; // 2/3 odds
                if (!charmProc)
                    continue;

                yield return info.GetFrame(prev, LeadRequired.CuteCharm);
            }
        }
    }
}
