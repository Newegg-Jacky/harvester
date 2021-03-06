﻿using Diagnostics.Tracing;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Harvester.Analysis
{
    /// <summary>
    /// Represents a base class that can performm resampling and transforms
    /// the trace & hardware counters data into queryable data.
    /// </summary>
    public abstract class EventProcessor
    {
        #region Constructors
        protected readonly TraceLog TraceLog;
        protected readonly TraceCounter[] Counters;
        private readonly Dictionary<int, ContextSwitch> LastSwitch =
            new Dictionary<int, ContextSwitch>();

        protected DateTime Start;
        protected DateTime End;
        protected TimeSpan Duration;
        protected TimeSpan Interval;
        protected int Count;
        protected int CoreCount;
        protected TraceProcess Process;
        protected TraceThread[] Threads;
        protected EventFrame[] Frames;
        protected PageFault[] Faults;

        /// <summary>
        /// Constructs a new processor for the provided data files.
        /// </summary>
        /// <param name="events">The data file containing events.</param>
        /// <param name="counters">The data file containing harware coutnters.</param>
        public EventProcessor(TraceLog events, TraceCounter[] counters)
        {
            // Add our data sources
            this.TraceLog = events;
            this.Counters = counters;
            this.CoreCount = this.Counters.Max(c => c.Core) + 1;

            // A last switch per core
            for (int i = 0; i < this.CoreCount; ++i)
                this.LastSwitch.Add(i, null);
        }
        #endregion

        #region Frame Members
        /// <summary>
        /// Gets the sampling frames. This is used to analyze what the threads were doing 
        /// and splits the data into workable fixed intervals.
        /// </summary>
        /// <param name="processName">The process to analyze.</param>
        /// <param name="interval">The interval (in milliseconds) of a frame.</param>
        /// <returns></returns>
        private EventFrame[] GetFrames(string processName, ushort interval)
        {
            // Get the proces to monitor
            this.Process = this.TraceLog.Processes
             .Where(p => p.Name.StartsWith(processName))
             .FirstOrDefault();

            // Get the threads
            this.Threads = this.Process.Threads
                .ToArray();

            // Define the timeframe of the experiment
            this.Start = new DateTime(Math.Max(this.Process.StartTime.Ticks, this.Counters.First().Time.Ticks));
            this.End = new DateTime(Math.Min(this.Process.EndTime.Ticks, this.Counters.Last().Time.Ticks));
            this.Duration = this.End - this.Start;
            this.Interval = TimeSpan.FromMilliseconds(interval);
            this.Count = (int)Math.Ceiling(this.Duration.TotalMilliseconds / this.Interval.TotalMilliseconds);

            Console.WriteLine("Analysis: Analyzing {0} process with {1} threads.", this.Process.Name, this.Threads.Length);
            Console.WriteLine("Analysis: duration = {0}", this.Duration);
            Console.WriteLine("Analysis: #cores = {0}", CoreCount);
            Console.WriteLine("Analysis: Creating #{0} frames for {1}ms. interval...", this.Count, this.Interval.TotalMilliseconds);
            
            // Get all context switches
            var switches = this.TraceLog.Events
                .Where(e => e.EventName.StartsWith("Thread/CSwitch"))
                .Select(sw => new ContextSwitch(sw))
                .ToArray();

            // Gets all page faults 
            this.Faults = this.TraceLog.Events
                .Where(e => e.EventName.StartsWith("PageFault"))
                .Select(e => new PageFault(e))
                .ToArray();

            // The list for our results
            var result = new List<EventFrame>();

            // Upsample at the specified interval
            for (int i = 0; i < this.Count; ++i)
            {
                // Current time
                var t = (int)this.Interval.TotalMilliseconds * i;
               
                // The interval starting time
                var timeFrom = this.Start + TimeSpan.FromMilliseconds(t);
                var timeTo = this.Start + TimeSpan.FromMilliseconds(t) + this.Interval;

                // For each core
                for (int core = 0; core < this.CoreCount; ++core)
                {
                    // Get corresponding context switches that happened on that particular core in the specified time frame
                    var cs = switches
                        .Where(e => e.TimeStamp >= timeFrom && e.TimeStamp <= timeTo)
                        .Where(e => e.ProcessorNumber == core)
                        .OrderBy(e => e.TimeStamp100ns);
                    // Console.WriteLine("Analysis: t = {0}, core = {1}, #hw = {2}, #cs = {3}", t, core, hw.Count(), cs.Count());

                    // Get an individual frame
                    result.Add(
                        GetFrame(timeFrom, core, cs)
                        );
                }
            }

            // Return the resulting frames
            return result.ToArray();
        }

        /// <summary>
        /// Gets one frame.
        /// </summary>
        private EventFrame GetFrame(DateTime time, int core, IEnumerable<ContextSwitch> switches)
        {
            // We got some events, calculate the proportion
            var fileTime = time.ToFileTime();
            var process  = this.Process.ProcessID;
            var maxTime  = (double)(this.Interval.TotalMilliseconds * 10000);

            // Construct a new frame
            var frame = new EventFrame(time, this.Interval, core);
            var previous = 0L;

            foreach (var sw in switches)
            {
                // Old thread id & process id
                var thread = EventThread.FromTrace(sw.OldThreadId, sw.OldProcessId, this.Process);
                var state = sw.State;

                // Set the time
                var current = sw.TimeStamp100ns - fileTime;
                var elapsed = current - previous;
                previous = current;

                // What's the current running thread?
                this.LastSwitch[core] = sw;

                

                // Add to our frame
                frame.Increment(thread, state, elapsed);
            }

            // If there was no switches during this period of time, take the last running
            if (frame.Total == 0)
            {
                var sw = this.LastSwitch[core];
                var thread = EventThread.FromTrace(sw.NewThreadId, sw.NewProcessId, this.Process);

                frame.Increment(thread, sw.State, maxTime);
            }


            // Get corresponding hardware counters 
            frame.HwCounters = this.GetCounters(core, time, time + this.Interval);

            // Get the page faults within this frame
            frame.PageFaults = this.GetPageFaults(core, time, time + this.Interval);

            return frame;
        }

        
        protected virtual PageFault[] GetPageFaults(int core, DateTime from, DateTime to)
        {
            return this.Faults
                .Where(e => e.Process == this.Process.ProcessID)
                .Where(e => e.TimeStamp >= from && e.TimeStamp <= to)
                .Where(e => e.ProcessorNumber == core)
                .ToArray();
        }


        /// <summary>
        /// Gets the counters for the specified core and time period.
        /// </summary>
        /// <param name="core">The core number.</param>
        /// <param name="from">The start time.</param>
        /// <param name="to">The end time.</param>
        /// <returns>The counters for that period.</returns>
        protected virtual EventHwCounters GetCounters(int core, DateTime from, DateTime to)
        {
 
            // Get corresponding hardware counters
            var hardware = this.Counters
                .Where(c => c.Time >= from && c.Time <= to)
                .Where(c => c.Core == core)
                .ToArray();

            // The total duration of the hw counter samples we have
            var duration = hardware
                .Where(c => c.Type == TraceCounterType.IPC)
                .Select(c => c.Duration / 1000)
                .Sum();

            var hwcount = hardware.Count();
            
            // If we don't have anything, return zeroes
            var counters = new EventHwCounters();


            // If we harve hardware counters
            if (hwcount == 0)
                return counters;

            // Absolute numbers
            counters.L2Misses = (long)((hardware.Where(c => c.Type == TraceCounterType.L2Miss).Select(c => c.Value).Sum() / duration) * this.Interval.TotalMilliseconds);
            counters.L3Misses = (long)((hardware.Where(c => c.Type == TraceCounterType.L3Miss).Select(c => c.Value).Sum() / duration) * this.Interval.TotalMilliseconds);
            counters.L2Hits = (long)((hardware.Where(c => c.Type == TraceCounterType.L2Hit).Select(c => c.Value).Sum() / duration) * this.Interval.TotalMilliseconds);
            counters.L3Hits = (long)((hardware.Where(c => c.Type == TraceCounterType.L3Hit).Select(c => c.Value).Sum() / duration) * this.Interval.TotalMilliseconds);

            // Average or sum depending on the counter
            counters.IPC = hardware.Where(c => c.Type == TraceCounterType.IPC).Select(c => c.Value).Average();
            counters.L2Clock = hardware.Where(c => c.Type == TraceCounterType.L2Clock).Select(c => c.Value).Average();
            counters.L3Clock = hardware.Where(c => c.Type == TraceCounterType.L3Clock).Select(c => c.Value).Average();

            // Computed
            counters.L1Misses = counters.L2Misses + counters.L2Hits;


            // The duration of the TLB events
            duration = hardware
                .Where(c => c.Type == TraceCounterType.TLBMiss)
                .Select(c => c.Duration / 1000)
                .Sum();

            counters.TLBMisses = (long)((hardware.Where(c => c.Type == TraceCounterType.TLBMiss).Select(c => c.Value).Sum() / duration) * this.Interval.TotalMilliseconds);

            return counters;
        }
        #endregion

        #region Analyze Members
        /// <summary>
        /// Invoked when an analysis needs to be performed.
        /// </summary>
        /// <returns>The ouptut of the analysis.</returns>
        protected abstract EventOutput OnAnalyze();

        /// <summary>
        /// Analyzes the process
        /// </summary>
        /// <param name="processName"></param>
        /// <param name="interval"></param>
        public EventOutput Analyze(string processName, ushort interval)
        {
            // First we need to gather frames
            this.Frames = this.GetFrames(processName, interval);

            Console.WriteLine("Analysis: Performing further analysis...");
            return this.OnAnalyze();
        }
        #endregion
    }


}
