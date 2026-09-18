using System;
using System.Drawing;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace YellowVitrine.Desktop;

/// <summary>
/// O programa fica RESIDENTE: sobe junto com o Windows (atalho no Startup com
/// o argumento --tray), some na bandeja, e "abrir" passa a ser só mostrar a
/// janela que já existe — instantâneo, por mais fraco que seja o caixa. Era o
/// tempo de abertura o problema todo: o Electron levava ~11 s nessa máquina.
///
/// Como o menu_caixa (COBOL) avisa o app: chamando o MESMO .exe de sempre.
/// Esta segunda instância percebe que já existe uma rodando, sinaliza pra ela
/// aparecer e encerra em seguida. Não precisa de arquivo texto, .bat, nem
/// vigiar pasta — e o COBOL não muda.
///
/// Se nenhuma instância estiver de pé (depois de uma queda, por exemplo), essa
/// mesma chamada vira a instância normal e abre a janela. Funciona dos dois
/// jeitos, sem caso especial.
/// </summary>
public partial class App : System.Windows.Application
{
    // Local\ e não Global\: um caixa tem uma sessão de usuário só, e Global
    // exigiria privilégio que o operador pode não ter.
    private const string NomeMutex = @"Local\YellowVitrine.Desktop.Instancia";
    private const string NomeEvento = @"Local\YellowVitrine.Desktop.Mostrar";

    private Mutex? _mutex;
    private EventWaitHandle? _evento;
    private Forms.NotifyIcon? _bandeja;
    private MainWindow? _janela;
    private bool _encerrandoDeVerdade;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(initiallyOwned: true, NomeMutex, out var souAPrimeira);

        if (!souAPrimeira)
        {
            // Já tem instância: acorda ela e sai. É este caminho que o COBOL
            // exercita a cada clique no menu.
            try
            {
                if (EventWaitHandle.TryOpenExisting(NomeEvento, out var ev))
                {
                    ev.Set();
                    ev.Dispose();
                }
            }
            catch { /* se não der pra sinalizar, não há o que fazer além de sair */ }
            Shutdown();
            return;
        }

        _evento = new EventWaitHandle(false, EventResetMode.AutoReset, NomeEvento);
        EscutarPedidosDeMostrar();

        _janela = new MainWindow();
        // Fechar a janela (botão Fechar, Alt+F4) só esconde — o processo fica
        // de pé pra próxima chamada ser instantânea.
        _janela.Closing += (_, args) =>
        {
            if (_encerrandoDeVerdade) return;
            args.Cancel = true;
            _janela.Hide();
        };

        MontarBandeja();

        // --tray = subiu junto com o Windows, começa escondido. Sem argumento =
        // alguém chamou o programa pra usar agora, então mostra.
        var comecarEscondido = Array.Exists(e.Args, a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (comecarEscondido) _janela.PrepararEmSegundoPlano();
        else Mostrar();
    }

    /// <summary>
    /// Fica esperando o sinal de outra instância. Thread de fundo dedicada em
    /// vez de timer: acorda no instante do Set(), sem intervalo de polling.
    /// </summary>
    private void EscutarPedidosDeMostrar()
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _evento!.WaitOne();
                    Dispatcher.Invoke(Mostrar);
                }
                catch (ObjectDisposedException) { return; }   // app encerrando
                catch { /* um sinal perdido não pode derrubar o app */ }
            }
        })
        { IsBackground = true, Name = "YellowVitrine.EscutaMostrar" };
        t.Start();
    }

    private void Mostrar()
    {
        if (_janela is null) return;
        _janela.Show();
        if (_janela.WindowState == WindowState.Minimized) _janela.WindowState = WindowState.Maximized;
        _janela.Activate();
        _janela.Topmost = true;   // truque pra vencer o bloqueio de foco do Windows
        _janela.Topmost = false;
        _janela.Focus();
        // Recarrega sempre: entre uma abertura e outra a vitrine pode ter
        // mudado (venda, outro caixa), então os dados na tela seriam velhos.
        _ = _janela.RecarregarAsync();
    }

    private void MontarBandeja()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => Mostrar());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => EncerrarDeVerdade());

        _bandeja = new Forms.NotifyIcon
        {
            Icon = IconeDaBandeja(),
            Text = "Yellow Vitrine",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _bandeja.DoubleClick += (_, _) => Mostrar();
    }

    /// <summary>Ícone desenhado em runtime — um círculo no amarelo da marca —
    /// pra não depender de um .ico versionado junto.</summary>
    private static Icon IconeDaBandeja()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var amarelo = new SolidBrush(Color.FromArgb(0xF9, 0xD6, 0x00));
            g.FillEllipse(amarelo, 1, 1, 30, 30);
            using var preto = new Pen(Color.FromArgb(0x33, 0, 0, 0), 2f);
            g.DrawEllipse(preto, 1, 1, 29, 29);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private void EncerrarDeVerdade()
    {
        _encerrandoDeVerdade = true;
        if (_bandeja is not null) { _bandeja.Visible = false; _bandeja.Dispose(); }
        Shutdown();
    }

    /// <summary>Desligar/deslogar o Windows encerra de verdade — é o "fecha
    /// quando o PC desliga" pedido.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        EncerrarDeVerdade();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_bandeja is not null) { _bandeja.Visible = false; _bandeja.Dispose(); }
        _evento?.Dispose();
        if (_mutex is not null) { try { _mutex.ReleaseMutex(); } catch { } _mutex.Dispose(); }
        base.OnExit(e);
    }
}
