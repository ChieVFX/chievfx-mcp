#nullable enable
using System;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal sealed class ProfilerToolService : IChievfxMcpToolHandler
    {
        private readonly Func<object> getState;
        private readonly Func<JToken, object> startRecording;
        private readonly Func<JToken, object> stopRecording;
        private readonly Func<object> getCounters;
        private readonly Func<JToken, object> controlWindow;
        private readonly Func<JToken, object> controlFrameDebugger;
        private readonly Func<JToken, object> listFrameDebuggerEvents;
        private readonly Func<JToken, object> getFrameDebuggerEvent;
        private readonly Func<JToken, object> listFrameDebuggerGroups;
        private readonly Func<JToken, object> listFrameDebuggerGroupEvents;
        private readonly Func<JToken, object> getFrameDebuggerDrawCall;
        private readonly Func<JToken, object> captureFrameDebuggerDrawCall;
        private readonly Func<JToken, object> pickFrameDebuggerPixel;

        public ProfilerToolService(
            Func<object> getState,
            Func<JToken, object> startRecording,
            Func<JToken, object> stopRecording,
            Func<object> getCounters,
            Func<JToken, object> controlWindow,
            Func<JToken, object> controlFrameDebugger,
            Func<JToken, object> listFrameDebuggerEvents,
            Func<JToken, object> getFrameDebuggerEvent,
            Func<JToken, object> listFrameDebuggerGroups,
            Func<JToken, object> listFrameDebuggerGroupEvents,
            Func<JToken, object> getFrameDebuggerDrawCall,
            Func<JToken, object> captureFrameDebuggerDrawCall,
            Func<JToken, object> pickFrameDebuggerPixel)
        {
            this.getState = getState;
            this.startRecording = startRecording;
            this.stopRecording = stopRecording;
            this.getCounters = getCounters;
            this.controlWindow = controlWindow;
            this.controlFrameDebugger = controlFrameDebugger;
            this.listFrameDebuggerEvents = listFrameDebuggerEvents;
            this.getFrameDebuggerEvent = getFrameDebuggerEvent;
            this.listFrameDebuggerGroups = listFrameDebuggerGroups;
            this.listFrameDebuggerGroupEvents = listFrameDebuggerGroupEvents;
            this.getFrameDebuggerDrawCall = getFrameDebuggerDrawCall;
            this.captureFrameDebuggerDrawCall = captureFrameDebuggerDrawCall;
            this.pickFrameDebuggerPixel = pickFrameDebuggerPixel;
        }

        public bool TryRunTool(string toolName, JToken args, out object? result)
        {
            result = toolName switch
            {
                "profiler-get-state" => getState(),
                "profiler-start-recording" => startRecording(args),
                "profiler-stop-recording" => stopRecording(args),
                "profiler-counters-get" => getCounters(),
                "profiler-window-control" => controlWindow(args),
                "frame-debugger-control" => controlFrameDebugger(args),
                "frame-debugger-events-list" => listFrameDebuggerEvents(args),
                "frame-debugger-event-get" => getFrameDebuggerEvent(args),
                "frame-debugger-groups-list" => listFrameDebuggerGroups(args),
                "frame-debugger-group-events-list" => listFrameDebuggerGroupEvents(args),
                "frame-debugger-drawcall-get" => getFrameDebuggerDrawCall(args),
                "frame-debugger-drawcall-screenshot" => captureFrameDebuggerDrawCall(args),
                "frame-debugger-pick-pixel" => pickFrameDebuggerPixel(args),
                _ => null
            };
            return result != null;
        }
    }
}
