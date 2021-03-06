﻿using Diagnostics.Tracing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Harvester.Analysis
{
    /// <summary>
    /// Does a simple cache miss processing.
    /// </summary>
    public class SimpleProcessor : EventProcessor
    {
        #region Constructor
                /// <summary>
        /// Constructs a new processor for the provided data files.
        /// </summary>
        /// <param name="events">The data file containing events.</param>
        /// <param name="counters">The data file containing harware coutnters.</param>
        public SimpleProcessor(TraceLog events, TraceCounter[] counters): base(events, counters)
        {

        }
        #endregion

        /// <summary>
        /// Invoked when an analysis needs to be performed.
        /// </summary>
        protected override EventOutput OnAnalyze()
        {
            // Here we will store our results
            var output  = new EventOutput();
            
            // Process every frame
            foreach(var frame in this.Frames)
            {
                // Build some shortcuts
                var core = frame.Core;
                
                // Get corresponding hardware counters 
                var cn = frame.HwCounters;

                // Process every thread within this frame
                foreach (var thread in frame.Threads)
                {
                    // Get the multiplier for that thread
                    var multiplier = frame.GetShare(thread);

                    // Get the number of demand zero faults.
                    var dzf = frame.PageFaults
                        .Where(pf => pf.Type == PageFaultType.Minor)
                        .Where(pf => pf.Thread == thread.Tid && pf.Process == thread.Pid)
                        .Count();

                    // Get the number of har" page faults.
                    var hpf = frame.PageFaults
                        .Where(pf => pf.Type == PageFaultType.Major)
                        .Where(pf => pf.Thread == thread.Tid && pf.Process == thread.Pid)
                        .Count();

                    output.Add("l1miss", frame, thread, Math.Round(multiplier * cn.L1Misses));
                    output.Add("l2miss", frame, thread, Math.Round(multiplier * cn.L2Misses));
                    output.Add("l3miss", frame, thread, Math.Round(multiplier * cn.L3Misses));
                    output.Add("tlbmiss", frame, thread, Math.Round(multiplier * cn.TLBMisses));

                    output.Add("l2perf", frame, thread, cn.L2Clock);
                    output.Add("l3perf", frame, thread, cn.L3Clock);

                    output.Add("dzf", frame, thread, dzf);
                    output.Add("hpf", frame, thread, hpf);
                    output.Add("ipc", frame, thread, cn.IPC);

                }
     
            }


            // Return the results
            return output;
        }


    }
}
