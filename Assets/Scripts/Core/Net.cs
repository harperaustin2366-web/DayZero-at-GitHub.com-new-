// INet: stub for scripts/core/net.gd (Godot autoload "Net", 1,347 lines: ENet LAN host/join,
// UDP discovery, online relay rooms, lobby, rejoin, RPC action forwarding). Not ported.
//
// game.gd itself only calls Net.sim_running(). Everything else flows the other way: net.gd
// drives Game. When porting net.gd, it needs these Game members (all present in the C# port):
//
//   state         S (cols, players, mode, phase, t, log, loot, srch, srch_c), SW, SH, SECT,
//                 MAX_COLONIES, CLIENT_GLIDE_S, NET_COLD, MaxZombies
//   flags         Running, IsClient, Mp, ViewCol, CurCol, LogCol, Actor
//   lifecycle     NewRun(diff, seed, ncols), AddColony(name), AssignSpawns(), LoadGame(path)
//   commands      DoAction(ci, action, args, who)       (host executes client requests)
//                 ApplyChoice(ev, choice)                (decision answers)
//                 MergeColonies(from, into), AddLog(text, kind)
//   snapshots     Serialize(includeZ, net, withCols), NetColony(ci, full, seen), SeenSectors(ci, cam)
//                 PackZombiesMany(views, origins)        (host -> clients)
//                 ApplyState(json, myCol), ApplyZombies(buf, dur, origin), SetClientTarget(p, to, dur)
//   queries       ColonyAt(ci), ColAlive(c), PersonById(pid), AllPeople(), IsWalking(p), Use(ci)
//   events        Changed, LogAdded, DecisionAdded, SfxRequested, ColonyOver
//   per colony    Colony.Away (net.gd sets "away" when a player drops; CampAuto honours it)
//
// Wire format note: Godot sent Variant dictionaries (PackedByteArray etc.) over ENet RPCs. The C#
// snapshots are JObjects; byte arrays become base64 strings (ApplyState decodes zinf / ctrl_h /
// active / loot_q / srch_q from base64).
namespace DayZero.Core
{
    public interface INet
    {
        /// <summary>net.gd sim_running(): on a client, whether the host's simulation is running;
        /// otherwise Game.Running.</summary>
        bool SimRunning();

        /// <summary>net.gd is_mp(): a multiplayer session is active and in game.</summary>
        bool IsMp { get; }

        /// <summary>True on the hosting peer.</summary>
        bool IsHost { get; }
    }

    /// <summary>Single-player stand-in.</summary>
    public sealed class OfflineNet : INet
    {
        readonly Game _g;
        public OfflineNet(Game g) { _g = g; }
        public bool SimRunning() => _g.Running;
        public bool IsMp => false;
        public bool IsHost => true;
    }
}
