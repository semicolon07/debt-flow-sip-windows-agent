using System.Text.Json;

namespace DebtFlow.SipAgent.Host;

public static class AgentSettingsProvisioner
{
    public static bool EnsureFromPackagedExample(
        string? configurationPath = null,
        string? examplePath = null)
    {
        string target = configurationPath ?? AgentRuntimeOptions.GetConfigurationPath();
        if (File.Exists(target)) return true;

        string source = examplePath ?? Path.Combine(AppContext.BaseDirectory, "agentsettings.example.json");
        if (!File.Exists(source)) return false;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(source);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return TryCreate(target, bytes);
    }

    public static bool TrySave(string configurationPath, bool isAllowAllOrigins, IReadOnlyList<string> allowedOrigins)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new { agent = new { isAllowAllOrigins, allowedOrigins } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        return TryCreate(configurationPath, bytes, replaceInvalidExisting: true);
    }

    private static bool TryCreate(string target, byte[] bytes, bool replaceInvalidExisting = false)
    {
        string? directory = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(directory)) return false;
        string temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(temporary, bytes);
            AgentRuntimeOptions validation = AgentRuntimeOptions.Load([], temporary);
            if (!validation.IsOperational) return false;

            if (replaceInvalidExisting && File.Exists(target))
            {
                File.Move(temporary, target, true);
                return true;
            }

            try
            {
                File.Move(temporary, target, false);
                return true;
            }
            catch (IOException) when (File.Exists(target))
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}

internal sealed class OriginConfigurationDialog : Form
{
    private readonly CheckBox _allowAll = new()
    {
        AutoSize = true,
        Checked = true,
        Text = "Allow all portal origins"
    };
    private readonly TextBox _origins = new()
    {
        AcceptsReturn = true,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Enabled = false
    };

    internal OriginConfigurationDialog()
    {
        Text = "Debt Flow SIP Agent setup";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 300);

        var instruction = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(480, 0),
            Text = "Choose whether this local Agent accepts every portal origin. If disabled, enter one exact http/https origin per line."
        };
        var originsLabel = new Label { AutoSize = true, Text = "Allowed origins" };
        var save = new Button { AutoSize = true, DialogResult = DialogResult.OK, Text = "Save" };
        var cancel = new Button { AutoSize = true, DialogResult = DialogResult.Cancel, Text = "Cancel" };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 5,
            Dock = DockStyle.Fill,
            Padding = new Padding(20)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(instruction, 0, 0);
        layout.Controls.Add(_allowAll, 0, 1);
        layout.Controls.Add(originsLabel, 0, 2);
        layout.Controls.Add(_origins, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;

        _allowAll.CheckedChanged += (_, _) => _origins.Enabled = !_allowAll.Checked;
        save.Click += (_, _) => ValidateBeforeClose();
    }

    internal bool IsAllowAllOrigins => _allowAll.Checked;

    internal IReadOnlyList<string> AllowedOrigins => _origins.Lines
        .Select(value => value.Trim())
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private void ValidateBeforeClose()
    {
        if (_allowAll.Checked) return;
        IReadOnlyList<string> origins = AllowedOrigins;
        string temporary = Path.Combine(Path.GetTempPath(), $"sip-agent-settings-{Guid.NewGuid():N}.json");
        try
        {
            if (origins.Count == 0 || !AgentSettingsProvisioner.TrySave(temporary, false, origins))
            {
                DialogResult = DialogResult.None;
                MessageBox.Show(
                    "Enter at least one exact http/https origin, for example https://portal.example.com.",
                    "Invalid origin configuration",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
