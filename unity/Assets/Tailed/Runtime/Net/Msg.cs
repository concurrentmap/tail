namespace Tailed.Net
{
    /// <summary>Message types. First byte of every payload.</summary>
    public enum Msg : byte
    {
        // client → host
        Hello = 1, Ready, PickVehicle, SubmitRoute, AddPin, RemovePin, Flag, CarState, Impact, Tag, StartMatch, Advance, ServiceHold,
        CarSwap, SpotterView, Voice,
        // host → client
        Welcome = 64, Lobby, PhaseInfo, Briefing, RouteStatus, Assign, PickupReveal, TeamPositions, Pins, FlagResult,
        Notice, Tagged, Debrief, Replay, TrafficSpawn, TrafficDespawn, TrafficSnapshot, BotCheat, Environment, VoiceOut,
        TargetReveal, MarkingInfo,
    }
}
