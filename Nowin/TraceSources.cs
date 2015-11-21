using System.Diagnostics;

namespace Nowin
{
    static class TraceSources
    {
        public static readonly ReleaseTraceSource Core = new ReleaseTraceSource("Nowin.Core");
        public static readonly DebugTraceSource CoreDebug = new DebugTraceSource("Nowin.Core.Debug");
    }

    class ReleaseTraceSource : TraceSource
    {
        public ReleaseTraceSource(string name) : base(name) { }
        public ReleaseTraceSource(string name, SourceLevels defaultLevel) : base(name, defaultLevel) { }

        /// <summary>
        /// Writes a warning message to the trace listeners in the System.Diagnostics.TraceSource.Listeners collection using the specified object array and formatting information.
        /// </summary>
        /// <param name="message">The informative message to write.</param>
        [Conditional("TRACE")]
        public void TraceWarning(string message) { TraceEvent(TraceEventType.Warning, 0, message); }

        /// <summary>
        /// Writes a warning message to the trace listeners in the System.Diagnostics.TraceSource.Listeners collection using the specified object array and formatting information.
        /// </summary>
        /// <param name="format">A composite format string (see Remarks) that contains text intermixed with zero or more format items, which correspond to objects in the args array.</param>
        /// <param name="args">An array containing zero or more objects to format.</param>
        [Conditional("TRACE")]
        public void TraceWarning(string format, params object[] args) { TraceEvent(TraceEventType.Warning, 0, format, args); }

        /// <summary>
        /// Writes an error message to the trace listeners in the System.Diagnostics.TraceSource.Listeners collection using the specified object array and formatting information.
        /// </summary>
        /// <param name="message">The informative message to write.</param>
        [Conditional("TRACE")]
        public void TraceError(string message) { TraceEvent(TraceEventType.Error, 0, message); }

        /// <summary>
        /// Writes an error message to the trace listeners in the System.Diagnostics.TraceSource.Listeners collection using the specified object array and formatting information.
        /// </summary>
        /// <param name="format">A composite format string (see Remarks) that contains text intermixed with zero or more format items, which correspond to objects in the args array.</param>
        /// <param name="args">An array containing zero or more objects to format.</param>
        [Conditional("TRACE")]
        public void TraceError(string format, params object[] args) { TraceEvent(TraceEventType.Error, 0, format, args); }
    }

    class DebugTraceSource : TraceSource
    {
        public DebugTraceSource(string name) : base(name) { }
        public DebugTraceSource(string name, SourceLevels defaultLevel) : base(name, defaultLevel) { }

        /// <summary>
        /// Writes an informational message to the trace listeners in the System.Diagnostics.TraceSource.Listeners collection using the specified object array and formatting information.
        /// </summary>
        /// <param name="message">The informative message to write.</param>
        [Conditional("DEBUG")]
        public new void TraceInformation(string message) { TraceEvent(TraceEventType.Information, 0, message); }

        /// <summary>
        /// Writes an informational message to the trace listeners in the System.Diagnostics.TraceSource.Listeners collection using the specified object array and formatting information.
        /// </summary>
        /// <param name="format">A composite format string (see Remarks) that contains text intermixed with zero or more format items, which correspond to objects in the args array.</param>
        /// <param name="args">An array containing zero or more objects to format.</param>
        [Conditional("DEBUG")]
        public new void TraceInformation(string format, params object[] args) { TraceEvent(TraceEventType.Information, 0, format, args); }

        /// <summary>
        /// Writes a warning message to the trace listeners in the System.Diagnostics.TraceSource.Listeners collection using the specified object array and formatting information.
        /// </summary>
        /// <param name="message">The informative message to write.</param>
        [Conditional("DEBUG")]
        public void TraceWarning(string message) { TraceEvent(TraceEventType.Warning, 0, message); }

        /// <summary>
        /// Writes a warning message to the trace listeners in the System.Diagnostics.TraceSource.Listeners collection using the specified object array and formatting information.
        /// </summary>
        /// <param name="format">A composite format string (see Remarks) that contains text intermixed with zero or more format items, which correspond to objects in the args array.</param>
        /// <param name="args">An array containing zero or more objects to format.</param>
        [Conditional("DEBUG")]
        public void TraceWarning(string format, params object[] args) { TraceEvent(TraceEventType.Warning, 0, format, args); }

        /// <summary>
        /// Writes an error message to the trace listeners in the System.Diagnostics.TraceSource.Listeners collection using the specified object array and formatting information.
        /// </summary>
        /// <param name="message">The informative message to write.</param>
        [Conditional("DEBUG")]
        public void TraceError(string message) { TraceEvent(TraceEventType.Error, 0, message); }

        /// <summary>
        /// Writes an error message to the trace listeners in the System.Diagnostics.TraceSource.Listeners collection using the specified object array and formatting information.
        /// </summary>
        /// <param name="format">A composite format string (see Remarks) that contains text intermixed with zero or more format items, which correspond to objects in the args array.</param>
        /// <param name="args">An array containing zero or more objects to format.</param>
        [Conditional("DEBUG")]
        public void TraceError(string format, params object[] args) { TraceEvent(TraceEventType.Error, 0, format, args); }
    }
}



/*********************
SAMPLE APP.CONFIG FILE
*********************/

/*
<?xml version="1.0" encoding="utf-8" ?>
<configuration>

  <system.diagnostics>

    <trace autoflush="true" indentsize="0">
      <listeners>
        <add name="Global"
             type="System.Diagnostics.TextWriterTraceListener"
             initializeData="Global.log">
        </add>
      </listeners>
    </trace>

    <sources>
      <source name="Nowin.Core">
        <listeners>
          <remove name="Default"/>
          <add name="Nowin.Core" />
        </listeners>
      </source>
      <source name="Nowin.Core.Debug">
        <listeners>
          <remove name="Default"/>
          <add name="Nowin.Core" />
        </listeners>
      </source>
    </sources>

    <switches>
      <add name="Nowin.Core" value="Verbose" />
      <add name="Nowin.Core.Debug" value="Verbose" />
    </switches>

    <sharedListeners>
      <add name="Nowin.Core"
           type="System.Diagnostics.TextWriterTraceListener"
           initializeData="Nowin.Core.log" />
    </sharedListeners>

  </system.diagnostics>

</configuration>
*/
