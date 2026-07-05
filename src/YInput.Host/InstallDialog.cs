using System.Drawing;
using System.Windows.Forms;

namespace YInput.Host;

/// <summary>설치본 최초 실행 시 뜨는 간단한 설치 대화상자 — 설치 위치 + 바로가기(시작 메뉴/바탕화면) 선택.</summary>
internal sealed class InstallDialog : Form
{
    private readonly TextBox _path;
    private readonly CheckBox _startMenu;
    private readonly CheckBox _desktop;

    public string InstallDir => _path.Text.Trim();
    public bool CreateStartMenu => _startMenu.Checked;
    public bool CreateDesktop => _desktop.Checked;

    public InstallDialog(string defaultDir)
    {
        Text = "Y Input 설치";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false; TopMost = true; ShowInTaskbar = true;
        ClientSize = new Size(468, 214);
        Font = new Font("Segoe UI", 9F);

        var lbl = new Label { Text = "Y Input을 설치할 위치와 바로가기를 정하세요.", Left = 14, Top = 14, Width = 440, Height = 20 };
        var lblPath = new Label { Text = "설치 위치", Left = 14, Top = 46, Width = 60, Height = 23, TextAlign = ContentAlignment.MiddleLeft };
        _path = new TextBox { Left = 78, Top = 44, Width = 286, Text = defaultDir };
        var browse = new Button { Text = "찾아보기…", Left = 370, Top = 43, Width = 84, Height = 25 };
        browse.Click += (_, _) =>
        {
            using var fb = new FolderBrowserDialog { Description = "설치할 상위 폴더 선택", UseDescriptionForTitle = true };
            try { fb.SelectedPath = Directory.GetParent(_path.Text.Trim())?.FullName ?? _path.Text.Trim(); } catch { /* 기본값 */ }
            if (fb.ShowDialog(this) == DialogResult.OK)
                _path.Text = Path.Combine(fb.SelectedPath, "YInput"); // 상위 폴더 아래 YInput 폴더로
        };

        _startMenu = new CheckBox { Text = "시작 메뉴 바로가기 만들기", Left = 78, Top = 84, Width = 320, Checked = true };
        _desktop = new CheckBox { Text = "바탕화면 바로가기 만들기", Left = 78, Top = 110, Width = 320, Checked = false };

        var note = new Label { Text = "이후 업데이트는 이 위치에서 자동으로 이뤄집니다.", Left = 14, Top = 144, Width = 440, Height = 20, ForeColor = Color.Gray };

        var ok = new Button { Text = "설치", Left = 276, Top = 174, Width = 84, Height = 28, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "취소", Left = 368, Top = 174, Width = 86, Height = 28, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_path.Text))
            {
                MessageBox.Show(this, "설치 위치를 입력하세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None; // 닫힘 취소
            }
        };

        Controls.AddRange(new Control[] { lbl, lblPath, _path, browse, _startMenu, _desktop, note, ok, cancel });
        AcceptButton = ok; CancelButton = cancel;
    }
}
