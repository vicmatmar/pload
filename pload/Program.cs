using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Text.RegularExpressions;
using System.Diagnostics;

using MinimalisticTelnet;

namespace pload
{
    class Program
    {
        /// <summary>
        /// The app folder where we save most logs, etc
        /// </summary>
        static string _app_data_dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pload");

        /// <summary>
        /// Prefix to custom pload and oinfo commands (soon to be auto configured)
        /// </summary>
        static string _cmd_prefix = "cs5480"; // SPI interface

        /// <summary>
        /// Voltage and current reference value
        /// </summary>
        static int _voltage_reference = 240;
        static int _current_reference = 15;

        /// <summary>
        /// Current/voltage structure
        /// </summary>
        public struct CS_Current_Voltage
        {
            public double Current;
            public double Voltage;

            public CS_Current_Voltage(double i = 0.0, double v = 0.0)
            {
                Current = i;
                Voltage = v;
            }
        }

        /// <summary>
        /// Process used to open Ember box ports (isachan=all)
        /// </summary>
        static Process _p_ember_isachan;

        /// <summary>
        /// Converts a 24bit hex (3 bytes) CS register value to a double
        /// </summary>
        /// <example>
        /// byte[] rx_data = new byte[3];
        /// rx_data[2] = 0x5c;
        /// rx_data[1] = 0x28;
        /// rx_data[0] = 0xf6;
        /// Should return midrange =~ 0.36
        /// </example>
        /// <param name="rx_data">data byte array byte[2] <=> MSB ... byte[0] <=> LSB</param>
        /// <returns>range 0 <= value < 1.0</returns>
        static double regHex_ToDouble(int data)
        {
            // Maximum 1 =~ 0xFFFFFF
            // Max rms 0.6 =~ 0x999999
            // Half rms 0.36 =~ 0x5C28F6
            double value = ((double)data) / 0x1000000; // 2^24
            return value;
        }

        /// <summary>
        /// Converts a hex string (3 bytes) CS register vaue to a double
        /// </summary>
        /// <param name="hexstr"></param>
        /// <returns>range 0 <= value < 1.0</returns>
        /// <seealso cref="double RegHex_ToDouble(int data)"/>
        static double regHex_ToDouble(string hexstr)
        {
            int val_int = Convert.ToInt32(hexstr, 16);
            return regHex_ToDouble(val_int); ;
        }

        /// <summary>
        /// Sends a pload command and returns the current and voltage values
        /// </summary>
        /// <param name="tc">Telnet connection to the EMber</param>
        /// <param name="board_type">What board are we using</param>
        /// <returns>Current/Voltage structure values</returns>
        static CS_Current_Voltage ember_parse_pinfo_registers(TelnetConnection tc)
        {
            string rawCurrentPattern = "Raw IRMS: ([0-9,A-F]{8})";
            string rawVoltagePattern = "Raw VRMS: ([0-9,A-F]{8})";
            double current_cs = 0.0;
            double voltage_cs = 0.0;

            string cmd = string.Format("cu {0}_pload", _cmd_prefix);
            traceLog(string.Format("Send cmd: {0}", cmd));
            tc.WriteLine(cmd);
            Thread.Sleep(500);
            string datain = tc.Read();
            Trace.WriteLine(datain);
            string msg;
            if (datain.Length > 0)
            {
                //traceLog(string.Format("Data received: {0}", datain)); // It gets log with "p_ember_isachan_OutputDataReceived"

                Match match = Regex.Match(datain, rawCurrentPattern);
                if (match.Groups.Count != 2)
                {
                    msg = string.Format("Unable to parse pinfo for current.  Output was:{0}", datain);
                    throw new Exception(msg);
                }

                string current_hexstr = match.Groups[1].Value;
                int current_int = Convert.ToInt32(current_hexstr, 16);
                current_cs = regHex_ToDouble(current_int);
                current_cs = current_cs * _current_reference / 0.6;

                voltage_cs = 0.0;
                match = Regex.Match(datain, rawVoltagePattern);
                if (match.Groups.Count != 2)
                {
                    msg = string.Format("Unable to parse pinfo for voltage.  Output was:{0}", datain);
                    throw new Exception(msg);
                }

                string voltage_hexstr = match.Groups[1].Value;
                int volatge_int = Convert.ToInt32(voltage_hexstr, 16);
                voltage_cs = regHex_ToDouble(volatge_int);
                voltage_cs = voltage_cs * _voltage_reference / 0.6;

            }
            else
            {
                msg = string.Format("No data recieved from telnet");
                throw new Exception(msg);
            }

            CS_Current_Voltage current_voltage = new CS_Current_Voltage(i: current_cs, v: voltage_cs);
            return current_voltage;
        }

        /// <summary>
        /// Starts the process responsible to open the Ember box isa channels
        /// </summary>
        static private void openEmberISAChannels()
        {
            _p_ember_isachan = new Process()
            {
                StartInfo = new ProcessStartInfo()
                {
                    FileName = Path.Combine(Properties.Settings.Default.Ember_BinPath, "em3xx_load.exe"),
                    Arguments = "--isachan=all",

                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = false
                }
            };
            _p_ember_isachan.EnableRaisingEvents = true;
            _p_ember_isachan.OutputDataReceived += p_ember_isachan_OutputDataReceived;
            _p_ember_isachan.ErrorDataReceived += p_ember_isachan_ErrorDataReceived;
            _p_ember_isachan.Start();
            _p_ember_isachan.BeginOutputReadLine();
            _p_ember_isachan.BeginErrorReadLine();

        }

        /// <summary>
        /// Writes to the trace
        /// </summary>
        /// <param name="txt"></param>
        static void traceLog(string txt)
        {
            string line = string.Format("{0:G}: {1}", DateTime.Now, txt);
            Trace.WriteLine(line);
        }

        static void p_ember_isachan_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                string str = "Error: " + e.Data;
                traceLog(str);
            }
        }

        static void p_ember_isachan_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            string str = e.Data;
            traceLog(str);
        }

        /// <summary>
        /// Closes the Ember process that open the isa channels
        /// <seealso cref="openEmberISAChannels"/>
        /// </summary>
        static void closeEmberISAChannels()
        {
            _p_ember_isachan.CancelErrorRead();
            _p_ember_isachan.Kill();
            _p_ember_isachan.Close();
        }

        /// <summary>
        /// Kills any em3xx_load process running in the system
        /// </summary>
        static void kill_em3xx_load()
        {
            try
            {
                Process[] processes = System.Diagnostics.Process.GetProcessesByName("em3xx_load");
                foreach (Process process in processes)
                {
                    process.Kill();
                }
            }
            catch (Exception ex)
            {
                string msg = string.Format("Error killing em3xx_load.\r\n{0}", ex.Message);
                traceLog(msg);
            }
        }

        static int Main(string[] args)
        {
            int rc = 0;

            Stream outResultsFile = File.Create("output.txt");
            var textListener = new TextWriterTraceListener(outResultsFile);
            Trace.Listeners.Add(textListener);
            string datafile_name = "power_data.txt";

            try
            {
                if (File.Exists(datafile_name))
                {
                    File.Delete(datafile_name);
                }

                kill_em3xx_load();

                openEmberISAChannels();

                TelnetConnection telnet_connection = new TelnetConnection("localhost", 4900);

                CS_Current_Voltage cv = ember_parse_pinfo_registers(telnet_connection);
                string msg = string.Format("Cirrus I = {0:F8}, V = {1:F8}, P = {2:F8}", cv.Current, cv.Voltage, cv.Current * cv.Voltage);
                traceLog(msg);

                msg = string.Format("{0:F8},{1:F8},{2:F8}", cv.Voltage, cv.Current, cv.Current * cv.Voltage);
                Console.WriteLine(msg);

                System.IO.StreamWriter file = new System.IO.StreamWriter(datafile_name);
                file.Write(msg);
                file.Close();

                closeEmberISAChannels();
            }
            catch (Exception ex)
            {
                rc = -1;
                traceLog(ex.Message);
                Console.WriteLine(ex.Message);
            }

            Trace.Flush();
            Trace.Close();

            return rc;
        }
    }
}
