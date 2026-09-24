using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace YellowVitrine.Desktop;

public partial class MainWindow : Window
{
    private Dados _dados = null!;
    private Operador? _operador;
    private bool _diaAberto;
    private bool _modoHomologacao;
    private bool _salvando;

    private readonly List<ProdutoVitrine> _produtos = [];

    /// <summary>
    /// Os produtos agrupados em faixas horizontais. A lista virtualiza por
    /// faixa: o VirtualizingStackPanel do WPF só trabalha em uma direção, e um
    /// WrapPanel comum não virtualiza nada — montaria os 130 cards de uma vez,
    /// que é exatamente o que trava esta máquina.
    /// </summary>
    private readonly List<List<ProdutoVitrine>> _faixas = [];

    /// <summary>Largura do card mais a margem, como está no DataTemplate.</summary>
    private const double LarguraDoCard = 210 + 12;

    private int _colunas;

    public MainWindow()
    {
        InitializeComponent();

        // Os campos nascem e morrem conforme a rolagem, então o tratamento fica
        // na lista e não em cada campo: handler preso a um card reciclado
        // passaria a valer para outro produto.
        ListaCards.AddHandler(UIElement.PreviewTextInputEvent, new TextCompositionEventHandler(AoDigitar), true);
        ListaCards.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(AoFocar), true);
        ListaCards.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(AoClicar), true);
        ListaCards.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(AoTeclar), true);

        Loaded += async (_, _) => await IniciarAsync();
    }

    /// <summary>
    /// Deixa a janela pronta sem aparecer: o processo sobe junto com o Windows
    /// e fica na bandeja, entao a primeira exibicao ja encontra tudo montado.
    /// E o que faz "abrir" ser instantaneo depois.
    /// </summary>
    public void PrepararEmSegundoPlano()
    {
        Opacity = 0;
        ShowInTaskbar = false;
        Show();
        Hide();
        Opacity = 1;
        ShowInTaskbar = true;
    }

    /// <summary>Recarrega do banco. Chamado toda vez que a janela reaparece —
    /// entre uma abertura e outra a vitrine pode ter mudado.</summary>
    public async Task RecarregarAsync() => await CarregarAsync();

    private async Task IniciarAsync() => await CarregarAsync();

    /// <summary>
    /// Config num appsettings.json ao lado do .exe — separado do .env do backend
    /// Node de propósito: as duas versões convivem, e reconfigurar uma não pode
    /// quebrar a outra.
    /// </summary>
    private sealed record Configuracao(string ConnectionString, bool ModoHomologacao, decimal QuantidadeDeHomologacao);

    private static Configuracao LerConfiguracao()
    {
        var caminho = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(caminho))
            throw new FileNotFoundException($"Arquivo de configuração não encontrado: {caminho}");

        using var doc = JsonDocument.Parse(File.ReadAllText(caminho));
        var raiz = doc.RootElement;

        if (!raiz.TryGetProperty("ConnectionString", out var cs) || string.IsNullOrWhiteSpace(cs.GetString()))
            throw new InvalidOperationException("appsettings.json não tem a chave \"ConnectionString\".");

        // As duas de homologação são opcionais: um appsettings.json de
        // instalação anterior não as tem, e a ausência significa desligado —
        // que é o comportamento normal. Atualizar o programa nunca liga o modo
        // de teste sozinho.
        var modo = raiz.TryGetProperty("ModoHomologacao", out var m) && m.ValueKind == JsonValueKind.True;

        var quantidade = raiz.TryGetProperty("QuantidadeDeHomologacao", out var q)
            && q.ValueKind == JsonValueKind.Number
            && q.TryGetDecimal(out var valor)
            && valor > 0
                ? valor
                : 100m;

        return new Configuracao(cs.GetString()!, modo, quantidade);
    }

    private async Task CarregarAsync()
    {
        EsconderErro();
        PainelEstado.Visibility = Visibility.Visible;
        TextoEstado.Text = "Carregando produtos…";
        ListaCards.ItemsSource = null;

        // Relê o appsettings.json a CADA carga, não só na subida do processo.
        // Como o app fica residente na bandeja, ler uma vez só significaria que
        // corrigir a senha do banco no arquivo não teria efeito nenhum até
        // alguém encerrar pela bandeja — armadilha silenciosa, e o sintoma
        // (segue sem conectar) não aponta pra causa.
        Configuracao config;
        try
        {
            config = LerConfiguracao();
            _dados = new Dados(config.ConnectionString);
        }
        catch (Exception ex)
        {
            MostrarErro(ex.Message);
            TextoEstado.Text = "Configuração inválida.";
            return;
        }

        try
        {
            // As três consultas são independentes; a tela só aparece quando as
            // três voltam.
            var tDia = _dados.DiaAbertoAsync();
            var tOperador = _dados.ResponsavelTurnoAbertoAsync();
            var tProdutos = _dados.ListarAsync();
            await Task.WhenAll(tDia, tOperador, tProdutos);

            _diaAberto = tDia.Result;
            _operador = tOperador.Result;
            _produtos.Clear();
            _produtos.AddRange(tProdutos.Result);
        }
        catch (Exception ex)
        {
            // Sem rotular de "falha de conexão": este catch cobre a conexão E
            // as três consultas. Dizer que foi conexão mandava investigar a
            // configuração quando o problema podia estar numa query — foi o
            // que aconteceu na primeira instalação.
            MostrarErro($"Falha ao carregar os dados: {ex.GetType().Name} — {ex.Message}");
            TextoEstado.Text = "Não foi possível carregar a vitrine.";
            return;
        }

        foreach (var produto in _produtos)
        {
            produto.Editavel = _diaAberto;
            produto.PropertyChanged += AoMudarProduto;
        }

        AplicarModoHomologacao(config);
        AtualizarTitulo();

        if (_produtos.Count == 0)
        {
            TextoEstado.Text = "Nenhum produto de vitrine encontrado.";
            PainelEstado.Visibility = Visibility.Visible;
            return;
        }

        PainelEstado.Visibility = Visibility.Collapsed;
        MontarFaixas(forcar: true);
        AtualizarBarraSalvar();
    }

    /// <summary>
    /// O título carrega o estado que antes ocupava faixas no topo. O modo
    /// homologação grava no estoque de verdade e o dia fechado impede salvar —
    /// os dois precisam de sinal, mas não de uma tarja permanente.
    /// </summary>
    private void AtualizarTitulo()
    {
        var partes = new List<string> { "Manutenção Vitrine" };
        if (_modoHomologacao) partes.Add("homologação");
        if (!_diaAberto) partes.Add("dia fechado");
        TituloCabecalho.Text = string.Join(" · ", partes);
    }

    // ------------------------------------------------------------- as faixas

    private int ColunasQueCabem()
    {
        // Desconta a barra de rolagem: sem isso o último card de cada faixa
        // fica meio escondido quando a lista passa a rolar.
        var largura = ListaCards.ActualWidth - 16;
        return largura <= 0 ? 1 : Math.Max(1, (int)(largura / LarguraDoCard));
    }

    private void MontarFaixas(bool forcar = false)
    {
        var colunas = ColunasQueCabem();

        // Só reorganiza quando a quantidade por linha muda de fato. Arrastar a
        // borda da janela dispara SizeChanged a cada pixel, e remontar a lista
        // em cada um deles engasga a máquina do caixa.
        if (!forcar && colunas == _colunas) return;
        _colunas = colunas;

        _faixas.Clear();
        for (var i = 0; i < _produtos.Count; i += colunas)
            _faixas.Add(_produtos.GetRange(i, Math.Min(colunas, _produtos.Count - i)));

        ListaCards.ItemsSource = null;
        ListaCards.ItemsSource = _faixas;
    }

    private void ListaCards_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) MontarFaixas();
    }

    // ------------------------------------------------------- campo do card

    private void AoMudarProduto(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProdutoVitrine.Pendente)) AtualizarBarraSalvar();
    }

    private static void AoDigitar(object sender, TextCompositionEventArgs e)
    {
        if (e.OriginalSource is TextBox && !e.Text.All(char.IsDigit)) e.Handled = true;
    }

    // Seleciona tudo ao focar: tocar no campo e digitar substitui, em vez de
    // acrescentar ao lado (mesma correção feita na versão Electron).
    private static void AoFocar(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.OriginalSource is TextBox campo) campo.SelectAll();
    }

    private static void AoClicar(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not TextBox campo && e.Source is not TextBox) return;
        campo = (TextBox)(e.OriginalSource as TextBox ?? e.Source);
        if (campo.IsKeyboardFocusWithin) return;
        e.Handled = true;
        campo.Focus();
    }

    /// <summary>
    /// Enter salta para o card seguinte, na ordem da tela. É o que torna
    /// viável lançar a vitrine inteira sem tirar a mão do teclado.
    /// </summary>
    private void AoTeclar(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.OriginalSource is not TextBox campo) return;
        e.Handled = true;

        if (campo.DataContext is not ProdutoVitrine atual) return;

        var i = _produtos.IndexOf(atual);
        if (i < 0 || i + 1 >= _produtos.Count)
        {
            // Último da lista: fica onde está. Voltar ao começo faria o
            // operador perder o lugar sem perceber.
            campo.SelectAll();
            return;
        }

        FocarProduto(_produtos[i + 1]);
    }

    private void FocarProduto(ProdutoVitrine produto)
    {
        var faixa = _faixas.FirstOrDefault(f => f.Contains(produto));
        if (faixa is null) return;

        ListaCards.ScrollIntoView(faixa);

        // Com virtualização o card pode ainda não existir na árvore visual: o
        // ScrollIntoView só agenda a rolagem. Buscar o campo agora acharia
        // nada, então espera o layout acontecer.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var destino = Descendentes(ListaCards)
                .OfType<TextBox>()
                .FirstOrDefault(c => ReferenceEquals(c.DataContext, produto));

            if (destino is null) return;
            destino.Focus();
            destino.SelectAll();
        }));
    }

    private static IEnumerable<DependencyObject> Descendentes(DependencyObject raiz)
    {
        var quantos = VisualTreeHelper.GetChildrenCount(raiz);
        for (var i = 0; i < quantos; i++)
        {
            var filho = VisualTreeHelper.GetChild(raiz, i);
            yield return filho;
            foreach (var neto in Descendentes(filho)) yield return neto;
        }
    }

    // ------------------------------------------------------------ o restante

    /// <summary>
    /// Modo de teste: já deixa os itens ZERADOS com a quantidade configurada
    /// pendente, para não ter que digitar item por item a cada dia aberto.
    ///
    /// Só nos zerados, de propósito. Quem já tem saldo mantém o que tem —
    /// somar em cima inflaria um número real, e a vitrine zera justamente no
    /// fechamento do dia, que é quando isto serve.
    ///
    /// Nada é gravado aqui: o valor entra como PENDENTE e continua passando
    /// pelo mesmo Salvar, com as mesmas travas.
    /// </summary>
    private void AplicarModoHomologacao(Configuracao config)
    {
        _modoHomologacao = config.ModoHomologacao;
        if (!_modoHomologacao) return;

        foreach (var produto in _produtos)
        {
            if (produto.QuantidadeSalva != 0) continue;
            produto.Pendente = config.QuantidadeDeHomologacao;
        }
    }

    private void AtualizarBarraSalvar()
    {
        var tem = _produtos.Any(p => p.Pendente != 0);
        BarraSalvar.Visibility = tem && _diaAberto ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Salvar_Click(object sender, RoutedEventArgs e)
    {
        if (_salvando) return;
        if (_operador is null)
        {
            MostrarErro("Não há turno aberto — não dá para identificar quem está alterando a vitrine.");
            return;
        }

        _salvando = true;
        BotaoSalvarTudo.IsEnabled = false;
        BotaoSalvarTudo.Content = "Salvando…";
        EsconderErro();

        // Cada produto é sua própria transação, igual ao saveChanges do app web —
        // um item que falhar não impede os outros.
        var pendentes = _produtos.Where(p => p.Pendente != 0).ToList();
        var falhas = new List<string>();
        foreach (var p in pendentes)
        {
            try { await _dados.AjustarAsync(p.Codfic, p.Pendente, _operador.Codusu); }
            catch (Exception ex) { falhas.Add($"{p.Nome}: {ex.Message}"); }
        }

        _salvando = false;
        BotaoSalvarTudo.IsEnabled = true;
        BotaoSalvarTudo.Content = "Salvar alterações";

        await CarregarAsync();
        if (falhas.Count > 0) MostrarErro(string.Join(" | ", falhas));
    }

    private void FecharApp_Click(object sender, RoutedEventArgs e) => Close();

    private void MostrarErro(string msg)
    {
        TextoErro.Text = msg;
        AvisoErro.Visibility = Visibility.Visible;
    }

    private void EsconderErro() => AvisoErro.Visibility = Visibility.Collapsed;
}
