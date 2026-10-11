using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace RadAudio.Settings
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // Opcoes do FlexASIO que este painel controla (ver CONFIGURATION.md do FlexASIO).
    sealed class AudioConfig
    {
        public string Backend = "Windows WASAPI";
        public int BufferSize = 512;
        public bool Exclusive;      // wasapiExclusiveMode (input e output)
        public bool MinLatency;     // suggestedLatencySeconds = 0.0 (input e output)
        public string InputDevice;  // null = dispositivo padrao, "" = desativado, senao o nome
        public string OutputDevice;
        public string InterfaceId;  // so do painel (gravado como comentario no .toml)
        public int Rate = 48000;    // so do painel: taxa usada para calcular buffer/latencia

        public AudioConfig Clone() { return (AudioConfig)MemberwiseClone(); }
    }

    sealed class Preset
    {
        public string Name;
        public string Description;
        public AudioConfig Config;   // BufferSize = valor base a 48 kHz
    }

    static class Toml
    {
        const string IfaceKey = "# rad-interface =";
        const string RateKey = "# rad-rate =";

        public static string ConfigPath
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RADAudio.toml"); }
        }

        static string Quote(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        static string Unquote(string raw)
        {
            raw = raw.Trim();
            if (raw.Length >= 2 && raw[0] == '"')
            {
                var sb = new StringBuilder();
                for (int i = 1; i < raw.Length; i++)
                {
                    char c = raw[i];
                    if (c == '\\' && i + 1 < raw.Length) { sb.Append(raw[++i]); continue; }
                    if (c == '"') break;
                    sb.Append(c);
                }
                return sb.ToString();
            }
            int hash = raw.IndexOf('#');
            return (hash >= 0 ? raw.Substring(0, hash) : raw).Trim();
        }

        public static AudioConfig Load()
        {
            if (!File.Exists(ConfigPath)) return null;
            var c = new AudioConfig { Backend = "Windows DirectSound" }; // padrao do FlexASIO sem a chave
            string section = "";
            foreach (string line0 in File.ReadAllLines(ConfigPath, Encoding.UTF8))
            {
                string line = line0.Trim();
                if (line.StartsWith(IfaceKey)) { c.InterfaceId = line.Substring(IfaceKey.Length).Trim(); continue; }
                if (line.StartsWith(RateKey))
                {
                    int r;
                    if (int.TryParse(line.Substring(RateKey.Length).Trim(), out r) && r > 0) c.Rate = r;
                    continue;
                }
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[') { section = line.Trim('[', ']', ' ').ToLowerInvariant(); continue; }
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = Unquote(line.Substring(eq + 1));
                if (section == "")
                {
                    if (key == "backend") c.Backend = val;
                    else if (key == "bufferSizeSamples") { int n; if (int.TryParse(val, out n)) c.BufferSize = n; }
                }
                else if (section == "input" || section == "output")
                {
                    if (key == "device") { if (section == "input") c.InputDevice = val; else c.OutputDevice = val; }
                    else if (key == "wasapiExclusiveMode" && val == "true") c.Exclusive = true;
                    else if (key == "suggestedLatencySeconds")
                    {
                        double d;
                        if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d == 0.0) c.MinLatency = true;
                    }
                }
            }
            return c;
        }

        public static void Save(AudioConfig c)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Gerado pelo RAD Audio Settings. Opcoes que o painel nao conhece nao sao preservadas");
            sb.AppendLine("# (uma copia do arquivo anterior fica em RADAudio.toml.bak).");
            if (c.InterfaceId != null) sb.AppendLine(IfaceKey + " " + c.InterfaceId);
            sb.AppendLine(RateKey + " " + c.Rate.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("backend = " + Quote(c.Backend));
            sb.AppendLine("bufferSizeSamples = " + c.BufferSize.ToString(CultureInfo.InvariantCulture));
            WriteSection(sb, "input", c, c.InputDevice);
            WriteSection(sb, "output", c, c.OutputDevice);
            if (File.Exists(ConfigPath)) File.Copy(ConfigPath, ConfigPath + ".bak", true);
            File.WriteAllText(ConfigPath, sb.ToString(), new UTF8Encoding(false));
        }

        static void WriteSection(StringBuilder sb, string name, AudioConfig c, string device)
        {
            sb.AppendLine();
            sb.AppendLine("[" + name + "]");
            if (device != null) sb.AppendLine("device = " + Quote(device));
            if (c.MinLatency) sb.AppendLine("suggestedLatencySeconds = 0.0");
            if (c.Exclusive) sb.AppendLine("wasapiExclusiveMode = true");
        }
    }

    // ---------- interfaces de audio (dados em interfaces.json) ----------

    sealed class Iface
    {
        public string Id;
        public string Name;
        public string Backend = "Windows WASAPI";
        public List<string> Match = new List<string>();   // trechos do nome que o Windows mostra
        public List<int> Rates = new List<int>();
        public int DefaultRate = 48000;
        public int[] Buffers = new int[3];                // base a 48 kHz: baixa, equilibrado, estavel (0 = padrao do perfil)

        public bool Matches(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return false;
            foreach (string m in Match)
                if (deviceName.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }

    static class Catalog
    {
        // O usuario pode sobrescrever sem precisar de administrador: %APPDATA%\RADAudio\interfaces.json
        public static string UserFile
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RADAudio", "interfaces.json"); }
        }
        public static string BundledFile
        {
            get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "interfaces.json"); }
        }

        public static List<Iface> Load(out string warning)
        {
            warning = null;
            foreach (string file in new[] { UserFile, BundledFile })
            {
                if (!File.Exists(file)) continue;
                try
                {
                    var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as IDictionary<string, object>;
                    object arr;
                    if (root == null || !root.TryGetValue("interfaces", out arr) || !(arr is IEnumerable))
                        throw new FormatException("campo \"interfaces\" ausente");
                    var list = new List<Iface>();
                    foreach (object o in (IEnumerable)arr)
                    {
                        Iface f = Parse(o as IDictionary<string, object>);
                        if (f != null) list.Add(f);
                    }
                    if (list.Count > 0) return list;
                }
                catch (Exception ex)
                {
                    warning = "interfaces.json inv\u00e1lido (" + ex.Message + "). Usando valores internos.";
                }
            }
            return BuiltIn();
        }

        static string Str(IDictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : null;
        }

        static Iface Parse(IDictionary<string, object> d)
        {
            if (d == null) return null;
            string name = Str(d, "name");
            if (string.IsNullOrEmpty(name)) return null;
            var f = new Iface { Name = name, Id = Str(d, "id") ?? name.ToLowerInvariant().Replace(' ', '-') };
            string backend = Str(d, "backend");
            if (backend != null) f.Backend = backend;
            object v;
            if (d.TryGetValue("match", out v) && v is IEnumerable && !(v is string))
                foreach (object m in (IEnumerable)v) { string s = Convert.ToString(m); if (!string.IsNullOrEmpty(s)) f.Match.Add(s); }
            if (f.Match.Count == 0) f.Match.Add(name);
            if (d.TryGetValue("sampleRates", out v) && v is IEnumerable)
                foreach (object r in (IEnumerable)v) f.Rates.Add(Convert.ToInt32(r));
            if (f.Rates.Count == 0) f.Rates.AddRange(new[] { 44100, 48000 });
            f.DefaultRate = f.Rates.Contains(48000) ? 48000 : f.Rates[0];
            if (d.TryGetValue("defaultRate", out v) && v != null)
            {
                int dr = Convert.ToInt32(v);
                if (f.Rates.Contains(dr)) f.DefaultRate = dr;
            }
            var b = d.TryGetValue("buffers", out v) ? v as IDictionary<string, object> : null;
            if (b != null)
            {
                string[] keys = { "low", "balanced", "stable" };
                for (int i = 0; i < 3; i++)
                    if (b.TryGetValue(keys[i], out v) && v != null) f.Buffers[i] = Convert.ToInt32(v);
            }
            return f;
        }

        // Usado se o interfaces.json estiver ausente ou quebrado.
        static List<Iface> BuiltIn()
        {
            return new List<Iface>
            {
                new Iface { Id = "rad-one-96", Name = "RAD One 96", Match = new List<string> { "Rad-One96" },
                    Rates = new List<int> { 44100, 48000, 88200, 96000 } },
                new Iface { Id = "rad-zero-48", Name = "RAD Zero 48", Match = new List<string> { "USB Audio Codec" },
                    Rates = new List<int> { 44100, 48000 } },
                new Iface { Id = "rad-two-192", Name = "RAD Two 192", Match = new List<string> { "Rad-Two192" },
                    Rates = new List<int> { 44100, 48000, 88200, 96000, 176400, 192000 } },
            };
        }
    }

    sealed class RateItem
    {
        public int Hz;
        public RateItem(int hz) { Hz = hz; }
        public override string ToString() { return (Hz / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + " kHz"; }
    }

    // ---------- dispositivos de audio (via PortAudioDevices.exe) ----------

    sealed class DeviceInfo
    {
        public string Name;
        public string Api;
        public int Inputs;
        public int Outputs;
    }

    static class Devices
    {
        // Roda o PortAudioDevices.exe instalado junto com o driver e le a lista de dispositivos.
        public static List<DeviceInfo> Query(out string error)
        {
            error = null;
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string exe = null;
            string[] archs = Environment.Is64BitOperatingSystem ? new[] { "x64", "x86" } : new[] { "x86" };
            foreach (string a in archs)
            {
                string p = System.IO.Path.Combine(dir, a, "PortAudioDevices.exe");
                if (File.Exists(p)) { exe = p; break; }
            }
            if (exe == null) { error = "PortAudioDevices.exe nao encontrado."; return new List<DeviceInfo>(); }

            var list = new List<DeviceInfo>();
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using (var proc = Process.Start(psi))
                {
                    proc.ErrorDataReceived += (s, e) => { };
                    proc.BeginErrorReadLine();
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(10000);

                    DeviceInfo cur = null;
                    foreach (string raw in output.Split('\n'))
                    {
                        string line = raw.Trim();
                        if (line.StartsWith("Device index:")) { cur = new DeviceInfo(); list.Add(cur); }
                        else if (cur == null) continue;
                        else if (line.StartsWith("Device name:"))
                        {
                            int a = line.IndexOf('"'), b = line.LastIndexOf('"');
                            if (a >= 0 && b > a) cur.Name = line.Substring(a + 1, b - a - 1);
                        }
                        else if (line.StartsWith("Host API name:")) cur.Api = line.Substring("Host API name:".Length).Trim();
                        else if (line.StartsWith("Input: max channel count ")) cur.Inputs = ParseCount(line, "Input: max channel count ");
                        else if (line.StartsWith("Output: max channel count ")) cur.Outputs = ParseCount(line, "Output: max channel count ");
                    }
                }
            }
            catch (Exception ex) { error = ex.Message; }
            return list;
        }

        static int ParseCount(string line, string prefix)
        {
            string rest = line.Substring(prefix.Length);
            int comma = rest.IndexOf(',');
            if (comma >= 0) rest = rest.Substring(0, comma);
            int n;
            return int.TryParse(rest.Trim(), out n) ? n : 0;
        }
    }

    // ---------- janela ----------

    sealed class MainForm : Form
    {
        // Paleta (mesma do mockup)
        static readonly Color Bg = Color.FromArgb(20, 22, 26);
        static readonly Color Panel = Color.FromArgb(28, 31, 37);
        static readonly Color Border = Color.FromArgb(42, 46, 54);
        static readonly Color Fg = Color.FromArgb(232, 234, 237);
        static readonly Color Muted = Color.FromArgb(154, 161, 172);
        static readonly Color Accent = Color.FromArgb(242, 240, 239);

        const string DefaultDevice = "Dispositivo padr\u00e3o";
        const string DisabledDevice = "Desativado";
        const string ManualInterface = "Outra interface (manual)";
        static readonly int[] StandardRates = { 44100, 48000, 88200, 96000, 176400, 192000 };

        // BufferSize = valor base a 48 kHz; o painel escala pela taxa de trabalho escolhida.
        readonly Preset[] _presets = new[]
        {
            new Preset { Name = "Baixa lat\u00eancia", Description = "Grava\u00e7\u00e3o e monitora\u00e7\u00e3o",
                Config = new AudioConfig { Backend = "Windows WASAPI", BufferSize = 480, Exclusive = true, MinLatency = true } },
            new Preset { Name = "Equilibrado", Description = "Uso geral",
                Config = new AudioConfig { Backend = "Windows WASAPI", BufferSize = 512 } },
            new Preset { Name = "Est\u00e1vel", Description = "Mixagem e projetos grandes",
                Config = new AudioConfig { Backend = "Windows WASAPI", BufferSize = 1024 } },
        };

        readonly string[] _backends = { "Windows WASAPI", "Windows WDM-KS", "Windows DirectSound", "MME" };
        readonly Button[] _presetButtons;
        readonly ComboBox _iface = new ComboBox();
        readonly Label _ifaceStatus = new Label();
        readonly ComboBox _backend = new ComboBox();
        readonly ComboBox _input = new ComboBox();
        readonly ComboBox _output = new ComboBox();
        readonly ComboBox _rate = new ComboBox();
        readonly NumericUpDown _buffer = new NumericUpDown();
        readonly Label _latency = new Label();
        readonly CheckBox _exclusive = new CheckBox();
        readonly CheckBox _minLatency = new CheckBox();
        readonly Label _subtitle = new Label();
        readonly Label _message = new Label();
        List<Iface> _catalog;
        Iface _current;                 // null = modo manual
        List<DeviceInfo> _devices = new List<DeviceInfo>();
        int _activePreset = -1;
        bool _loading;
        bool _autoDetect;

        public MainForm()
        {
            Text = "RAD Audio \u2013 Configura\u00e7\u00f5es";
            ClientSize = new Size(1040, 660);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 9.5f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            string warning;
            _catalog = Catalog.Load(out warning);

            _presetButtons = new Button[_presets.Length];
            BuildSidebar();
            BuildMain();
            if (warning != null) _message.Text = warning;

            AudioConfig cfg = null;
            try { cfg = Toml.Load(); }
            catch (Exception ex) { MessageBox.Show("N\u00e3o foi poss\u00edvel ler " + Toml.ConfigPath + ":\n" + ex.Message, Text); }

            int idx = cfg != null ? IndexOfInterface(cfg.InterfaceId) : -1;
            _autoDetect = (cfg == null || cfg.InterfaceId == null);
            Quiet(() =>
            {
                _iface.SelectedIndex = idx >= 0 ? idx : _catalog.Count;
                _current = idx >= 0 ? _catalog[idx] : null;
                FillRates(_current, cfg != null ? cfg.Rate : 48000);
                ApplyToControls(cfg ?? _presets[1].Config);
            });
            if (cfg != null) SetActivePreset(MatchPreset(cfg));
            else ApplyPreset(1);
            UpdateLatency();
            UpdateIfaceStatus();

            bool freshStart = (cfg == null);
            Shown += (s, e) => { OnStartup(freshStart); };
        }

        // ---------- construcao da interface ----------

        static Label MakeLabel(string text, int x, int y, Color color, float size, bool bold)
        {
            return new Label
            {
                Text = text, Left = x, Top = y, AutoSize = true, ForeColor = color,
                Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
            };
        }

        static Panel MakeCard(int x, int y, int w, int h, string title)
        {
            var p = new Panel { Left = x, Top = y, Width = w, Height = h, BackColor = Panel };
            p.Paint += (s, e) => e.Graphics.DrawRectangle(new Pen(Border), 0, 0, p.Width - 1, p.Height - 1);
            p.Controls.Add(MakeLabel(title, 16, 14, Fg, 11f, true));
            return p;
        }

        void StyleCombo(ComboBox c, int x, int y, int w, bool editable)
        {
            c.Left = x; c.Top = y; c.Width = w;
            c.DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Bg; c.ForeColor = Fg;
        }

        void BuildSidebar()
        {
            var side = new Panel { Left = 0, Top = 0, Width = 240, Height = 660, BackColor = Panel };
            side.Paint += (s, e) => e.Graphics.DrawLine(new Pen(Border), 239, 0, 239, 660);

            var logo = new PictureBox { Left = 24, Top = 24, Width = 44, Height = 44, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Panel };
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("rad_logo.png"))
                    if (s != null) logo.Image = Image.FromStream(s);
            }
            catch { }
            side.Controls.Add(logo);
            side.Controls.Add(MakeLabel("RAD Audio", 80, 26, Fg, 12f, true));
            side.Controls.Add(MakeLabel("Driver ASIO", 80, 50, Muted, 9f, false));
            side.Controls.Add(MakeLabel("PERFIS", 24, 100, Muted, 8f, true));

            for (int i = 0; i < _presets.Length; i++)
            {
                int idx = i;
                var b = new Button
                {
                    Left = 12, Top = 126 + i * 66, Width = 216, Height = 58,
                    Text = _presets[i].Name + Environment.NewLine + _presets[i].Description,
                    TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0),
                    FlatStyle = FlatStyle.Flat, BackColor = Panel, ForeColor = Fg, UseVisualStyleBackColor = false
                };
                b.FlatAppearance.BorderSize = 1;
                b.Click += (s, e) => { ApplyPreset(idx); };
                _presetButtons[i] = b;
                side.Controls.Add(b);
            }

            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            side.Controls.Add(MakeLabel("v" + ver.ToString(3) + " \u00b7 RAD Audio", 24, 620, Muted, 8.5f, false));
            Controls.Add(side);
        }

        void BuildMain()
        {
            Controls.Add(MakeLabel("Configura\u00e7\u00f5es", 272, 28, Fg, 20f, true));
            _subtitle.AutoSize = true; _subtitle.Left = 272; _subtitle.Top = 68; _subtitle.ForeColor = Muted;
            Controls.Add(_subtitle);

            // Seletor de interface (canto superior direito)
            Controls.Add(MakeLabel("Interface", 700, 20, Muted, 8.5f, true));
            StyleCombo(_iface, 700, 40, 308, false);
            foreach (Iface f in _catalog) _iface.Items.Add(f.Name);
            _iface.Items.Add(ManualInterface);
            _iface.SelectedIndexChanged += (s, e) => { if (!_loading) SelectInterface(_iface.SelectedIndex, true); };
            Controls.Add(_iface);
            _ifaceStatus.AutoSize = false; _ifaceStatus.AutoEllipsis = true;
            _ifaceStatus.Left = 700; _ifaceStatus.Top = 72; _ifaceStatus.Width = 308; _ifaceStatus.Height = 20;
            _ifaceStatus.ForeColor = Muted; _ifaceStatus.Font = new Font("Segoe UI", 8.5f);
            Controls.Add(_ifaceStatus);

            // Cartao: dispositivos
            var dev = MakeCard(272, 108, 358, 290, "Dispositivos");
            dev.Controls.Add(MakeLabel("Backend", 16, 46, Muted, 8.5f, true));
            StyleCombo(_backend, 16, 66, 326, false);
            _backend.Items.AddRange(_backends);
            _backend.SelectedIndexChanged += (s, e) => { if (!_loading) { RefreshDeviceLists(); Touched(); } };
            dev.Controls.Add(_backend);

            dev.Controls.Add(MakeLabel("Entrada", 16, 104, Muted, 8.5f, true));
            StyleCombo(_input, 16, 124, 326, true);
            _input.TextChanged += (s, e) => { if (!_loading) Touched(); };
            dev.Controls.Add(_input);

            dev.Controls.Add(MakeLabel("Sa\u00edda", 16, 162, Muted, 8.5f, true));
            StyleCombo(_output, 16, 182, 326, true);
            _output.TextChanged += (s, e) => { if (!_loading) Touched(); };
            dev.Controls.Add(_output);

            var refresh = new Button
            {
                Text = "Atualizar lista de dispositivos", Left = 16, Top = 232, Width = 326, Height = 40,
                FlatStyle = FlatStyle.Flat, BackColor = Panel, ForeColor = Fg, UseVisualStyleBackColor = false
            };
            refresh.FlatAppearance.BorderColor = Border;
            refresh.Click += (s, e) => { RefreshDevices(true); UpdateIfaceStatus(); };
            dev.Controls.Add(refresh);
            Controls.Add(dev);

            // Cartao: audio
            var audio = MakeCard(650, 108, 358, 290, "\u00c1udio");
            audio.Controls.Add(MakeLabel("Taxa de trabalho", 16, 46, Muted, 8.5f, true));
            StyleCombo(_rate, 16, 66, 160, false);
            _rate.SelectedIndexChanged += (s, e) =>
            {
                if (_loading) return;
                if (_activePreset >= 0) ApplyPreset(_activePreset); else UpdateLatency();
            };
            audio.Controls.Add(_rate);

            audio.Controls.Add(MakeLabel("Tamanho do buffer (samples)", 16, 108, Muted, 8.5f, true));
            _buffer.Left = 16; _buffer.Top = 128; _buffer.Width = 160;
            _buffer.Minimum = 16; _buffer.Maximum = 16384; _buffer.Increment = 16; _buffer.Value = 512;
            _buffer.BackColor = Bg; _buffer.ForeColor = Fg; _buffer.BorderStyle = BorderStyle.FixedSingle;
            _buffer.ValueChanged += (s, e) => { UpdateLatency(); if (!_loading) Touched(); };
            audio.Controls.Add(_buffer);

            audio.Controls.Add(MakeLabel("Lat\u00eancia estimada", 16, 176, Muted, 8.5f, true));
            _latency.AutoSize = true; _latency.Left = 16; _latency.Top = 194; _latency.ForeColor = Accent;
            _latency.Font = new Font("Segoe UI", 24f, FontStyle.Bold);
            audio.Controls.Add(_latency);
            audio.Controls.Add(MakeLabel("A taxa real \u00e9 definida pelo seu DAW. Aqui ela serve\npara calcular o buffer e a lat\u00eancia.", 16, 248, Muted, 8f, false));
            Controls.Add(audio);

            // Cartao: opcoes
            var opt = MakeCard(272, 418, 736, 100, "Op\u00e7\u00f5es");
            _exclusive.Text = "Modo exclusivo (WASAPI)";
            _exclusive.Left = 16; _exclusive.Top = 48; _exclusive.AutoSize = true; _exclusive.ForeColor = Fg;
            _exclusive.CheckedChanged += (s, e) => { if (!_loading) Touched(); };
            opt.Controls.Add(_exclusive);
            _minLatency.Text = "Pedir lat\u00eancia m\u00ednima ao Windows";
            _minLatency.Left = 300; _minLatency.Top = 48; _minLatency.AutoSize = true; _minLatency.ForeColor = Fg;
            _minLatency.CheckedChanged += (s, e) => { if (!_loading) Touched(); };
            opt.Controls.Add(_minLatency);
            Controls.Add(opt);

            Controls.Add(MakeLabel("Arquivo: " + Toml.ConfigPath, 272, 534, Muted, 8.5f, false));
            _message.AutoSize = false; _message.AutoEllipsis = true;
            _message.Left = 272; _message.Top = 596; _message.Width = 396; _message.Height = 40; _message.ForeColor = Muted;
            Controls.Add(_message);

            var restore = new Button
            {
                Text = "Restaurar padr\u00e3o", Left = 678, Top = 590, Width = 150, Height = 44,
                FlatStyle = FlatStyle.Flat, BackColor = Bg, ForeColor = Fg, UseVisualStyleBackColor = false
            };
            restore.FlatAppearance.BorderColor = Border;
            restore.Click += (s, e) =>
            {
                ApplyPreset(1);
                _message.Text = "Valores padr\u00e3o carregados. Clique em Aplicar para gravar.";
            };
            Controls.Add(restore);

            var apply = new Button
            {
                Text = "Aplicar altera\u00e7\u00f5es", Left = 838, Top = 590, Width = 170, Height = 44,
                FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.FromArgb(11, 18, 16),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), UseVisualStyleBackColor = false
            };
            apply.FlatAppearance.BorderSize = 0;
            apply.Click += (s, e) => ApplyChanges();
            Controls.Add(apply);
        }

        // ---------- logica ----------

        // Executa uma alteracao de controles sem disparar os handlers "usuario mexeu".
        void Quiet(Action a)
        {
            bool prev = _loading;
            _loading = true;
            try { a(); } finally { _loading = prev; }
        }

        void Touched() { SetActivePreset(-1); _message.Text = ""; }

        void SetActivePreset(int index)
        {
            _activePreset = index;
            for (int i = 0; i < _presetButtons.Length; i++)
                _presetButtons[i].FlatAppearance.BorderColor = (i == index) ? Accent : Panel;
            _subtitle.Text = "Perfil ativo: " + (index >= 0 ? _presets[index].Name : "Personalizado");
        }

        int CurrentRate
        {
            get
            {
                var item = _rate.SelectedItem as RateItem;
                return item != null ? item.Hz : 48000;
            }
        }

        static int Scale(int baseSamples, int rate)
        {
            if (rate == 48000) return baseSamples;
            double v = baseSamples * (rate / 48000.0);
            return Math.Max(16, (int)(Math.Round(v / 16.0) * 16.0));
        }

        static int PresetBuffer(Preset p, int index, Iface iface, int rate)
        {
            int baseSamples = (iface != null && iface.Buffers[index] > 0) ? iface.Buffers[index] : p.Config.BufferSize;
            return Scale(baseSamples, rate);
        }

        int IndexOfInterface(string id)
        {
            if (id == null) return -1;
            for (int i = 0; i < _catalog.Count; i++) if (_catalog[i].Id == id) return i;
            return -1;
        }

        void FillRates(Iface f, int wanted)
        {
            _rate.Items.Clear();
            IEnumerable<int> rates = f != null ? (IEnumerable<int>)f.Rates : StandardRates;
            RateItem pick = null, fallback = null;
            foreach (int hz in rates)
            {
                var item = new RateItem(hz);
                _rate.Items.Add(item);
                if (hz == wanted) pick = item;
                if (f != null && hz == f.DefaultRate) fallback = item;
                if (f == null && hz == 48000) fallback = item;
            }
            _rate.SelectedItem = pick ?? fallback ?? (_rate.Items.Count > 0 ? _rate.Items[0] : null);
        }

        void SetBackend(string b)
        {
            if (!_backend.Items.Contains(b)) _backend.Items.Add(b);
            _backend.SelectedItem = b;
        }

        void ApplyPreset(int index)
        {
            Preset p = _presets[index];
            Quiet(() =>
            {
                SetBackend(_current != null ? _current.Backend : p.Config.Backend);
                int v = PresetBuffer(p, index, _current, CurrentRate);
                _buffer.Value = Math.Max(_buffer.Minimum, Math.Min(_buffer.Maximum, v));
                _exclusive.Checked = p.Config.Exclusive;
                _minLatency.Checked = p.Config.MinLatency;
                RefreshDeviceLists();
            });
            SetActivePreset(index);
            UpdateLatency();
        }

        int MatchPreset(AudioConfig c)
        {
            for (int i = 0; i < _presets.Length; i++)
            {
                AudioConfig p = _presets[i].Config;
                string backend = _current != null ? _current.Backend : p.Backend;
                if (c.Backend == backend && c.BufferSize == PresetBuffer(_presets[i], i, _current, CurrentRate)
                    && c.Exclusive == p.Exclusive && c.MinLatency == p.MinLatency) return i;
            }
            return -1;
        }

        // Escolha do usuario (ou deteccao automatica) de uma interface.
        void SelectInterface(int index, bool fillDevices)
        {
            _current = (index >= 0 && index < _catalog.Count) ? _catalog[index] : null;
            Quiet(() =>
            {
                FillRates(_current, CurrentRate);
                if (_current != null) SetBackend(_current.Backend);
                RefreshDeviceLists();
                if (fillDevices)
                {
                    // Nao herdar os dispositivos da interface anterior.
                    _input.Text = DefaultDevice;
                    _output.Text = DefaultDevice;
                    if (_current != null) AutoSelectDevices(_current);
                }
            });
            if (_activePreset >= 0) ApplyPreset(_activePreset); else UpdateLatency();
            UpdateIfaceStatus();
        }

        // Preenche entrada/saida com os dispositivos da interface, no backend atual.
        void AutoSelectDevices(Iface f)
        {
            string api = Convert.ToString(_backend.SelectedItem);
            string inName = null, outName = null;
            foreach (DeviceInfo d in _devices)
            {
                if (d.Api != api || !f.Matches(d.Name)) continue;
                if (inName == null && d.Inputs > 0) inName = d.Name;
                if (outName == null && d.Outputs > 0) outName = d.Name;
            }
            if (inName != null) _input.Text = inName;
            if (outName != null) _output.Text = outName;
        }

        string DetectedDeviceName(Iface f)
        {
            foreach (DeviceInfo d in _devices) if (f.Matches(d.Name)) return d.Name;
            return null;
        }

        void UpdateIfaceStatus()
        {
            if (_current == null) { _ifaceStatus.Text = "Modo manual: escolha os dispositivos abaixo."; return; }
            string name = DetectedDeviceName(_current);
            _ifaceStatus.Text = name != null ? "Detectada: " + name : "N\u00e3o detectada (conecte e atualize a lista).";
        }

        void OnStartup(bool freshStart)
        {
            RefreshDevices(false);
            if (_autoDetect)
            {
                for (int i = 0; i < _catalog.Count; i++)
                {
                    if (DetectedDeviceName(_catalog[i]) == null) continue;
                    Quiet(() => _iface.SelectedIndex = i);
                    SelectInterface(i, freshStart);
                    break;
                }
            }
            UpdateIfaceStatus();
        }

        void UpdateLatency()
        {
            double ms = (double)_buffer.Value / CurrentRate * 1000.0;
            _latency.Text = ms.ToString("0.0", CultureInfo.CurrentCulture) + " ms";
        }

        static string DeviceToText(string device)
        {
            if (device == null) return DefaultDevice;
            if (device.Length == 0) return DisabledDevice;
            return device;
        }

        static string TextToDevice(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || text == DefaultDevice) return null;
            if (text == DisabledDevice) return "";
            return text;
        }

        void ApplyToControls(AudioConfig c)
        {
            SetBackend(c.Backend);
            _buffer.Value = Math.Max(_buffer.Minimum, Math.Min(_buffer.Maximum, c.BufferSize));
            _exclusive.Checked = c.Exclusive;
            _minLatency.Checked = c.MinLatency;
            FillDeviceList(_input, true, DeviceToText(c.InputDevice));
            FillDeviceList(_output, false, DeviceToText(c.OutputDevice));
        }

        AudioConfig ReadFromControls()
        {
            return new AudioConfig
            {
                Backend = Convert.ToString(_backend.SelectedItem) ?? _backends[0],
                BufferSize = (int)_buffer.Value,
                Exclusive = _exclusive.Checked,
                MinLatency = _minLatency.Checked,
                InputDevice = TextToDevice(_input.Text),
                OutputDevice = TextToDevice(_output.Text),
                InterfaceId = _current != null ? _current.Id : null,
                Rate = CurrentRate
            };
        }

        void FillDeviceList(ComboBox box, bool input, string selectedText)
        {
            string api = Convert.ToString(_backend.SelectedItem);
            box.Items.Clear();
            box.Items.Add(DefaultDevice);
            box.Items.Add(DisabledDevice);
            foreach (DeviceInfo d in _devices)
            {
                if (d.Api != api || string.IsNullOrEmpty(d.Name)) continue;
                if ((input ? d.Inputs : d.Outputs) <= 0) continue;
                if (!box.Items.Contains(d.Name)) box.Items.Add(d.Name);
            }
            if (!box.Items.Contains(selectedText)) box.Items.Add(selectedText);
            box.Text = selectedText;
        }

        // Troca de backend: refaz as listas; o que nao existe no novo backend volta para "padrao".
        void RefreshDeviceLists()
        {
            Quiet(() =>
            {
                string inText = _input.Text, outText = _output.Text;
                FillDeviceList(_input, true, DefaultDevice);
                FillDeviceList(_output, false, DefaultDevice);
                if (_input.Items.Contains(inText)) _input.Text = inText;
                if (_output.Items.Contains(outText)) _output.Text = outText;
            });
        }

        void RefreshDevices(bool showErrors)
        {
            Cursor = Cursors.WaitCursor;
            string error;
            var list = Devices.Query(out error);
            Cursor = Cursors.Default;
            if (error != null)
            {
                if (showErrors) MessageBox.Show("N\u00e3o foi poss\u00edvel listar os dispositivos:\n" + error, Text);
                return;
            }
            _devices = list;
            Quiet(() =>
            {
                string inText = _input.Text, outText = _output.Text;
                FillDeviceList(_input, true, inText);
                FillDeviceList(_output, false, outText);
            });
            _message.Text = list.Count + " dispositivos encontrados.";
        }

        void ApplyChanges()
        {
            try
            {
                Toml.Save(ReadFromControls());
                _message.Text = "Salvo \u00e0s " + DateTime.Now.ToString("HH:mm:ss") + ". O driver recarrega as configura\u00e7\u00f5es automaticamente.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("N\u00e3o foi poss\u00edvel salvar:\n" + ex.Message, Text);
            }
        }
    }
}
