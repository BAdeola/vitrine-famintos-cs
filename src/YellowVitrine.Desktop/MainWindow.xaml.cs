using System;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly List<ProdutoVitrine> _produtos = [];
    private readonly HashSet<string> _categoriasAbertas = [];
    private bool _salvando;

    public MainWindow()
    {
        InitializeComponent();
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
    private static string LerConnectionString()
    {
        var caminho = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(caminho))
            throw new FileNotFoundException($"Arquivo de configuração não encontrado: {caminho}");

        using var doc = JsonDocument.Parse(File.ReadAllText(caminho));
        if (!doc.RootElement.TryGetProperty("ConnectionString", out var cs) || string.IsNullOrWhiteSpace(cs.GetString()))
            throw new InvalidOperationException("appsettings.json não tem a chave \"ConnectionString\".");
        return cs.GetString()!;
    }

    private async Task CarregarAsync()
    {
        EsconderErro();
        PainelEstado.Visibility = Visibility.Visible;
        TextoEstado.Text = "Carregando produtos…";
        ListaCategorias.Children.Clear();

        // Relê o appsettings.json a CADA carga, não só na subida do processo.
        // Como o app fica residente na bandeja, ler uma vez só significaria que
        // corrigir a senha do banco no arquivo não teria efeito nenhum até
        // alguém encerrar pela bandeja — armadilha silenciosa, e o sintoma
        // (segue sem conectar) não aponta pra causa.
        try
        {
            _dados = new Dados(LerConnectionString());
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

        TituloCabecalho.Text = _operador is not null
            ? $"Turno de {_operador.Apelido.ToUpperInvariant()}"
            : "Atualização de Vitrine";
        AvisoDiaFechado.Visibility = _diaAberto ? Visibility.Collapsed : Visibility.Visible;

        if (_produtos.Count == 0)
        {
            TextoEstado.Text = "Nenhum produto de vitrine encontrado.";
            PainelEstado.Visibility = Visibility.Visible;
            return;
        }

        PainelEstado.Visibility = Visibility.Collapsed;
        MontarLista();
        AtualizarBarraSalvar();
    }

    // Pincéis resolvidos uma vez. FindResource percorre a árvore de recursos a
    // cada chamada, e antes isso acontecia várias vezes POR LINHA da lista.
    private Brush? _pTintaFraca, _pMarcaEscura, _pSuperficie, _pVermelho;
    private Brush? _pLinha;
    private Style? _sBotaoCategoria, _sBotaoRedondo, _sCampo;

    private void ResolverRecursos()
    {
        _pTintaFraca ??= (Brush)FindResource("TintaFraca");
        _pMarcaEscura ??= (Brush)FindResource("MarcaEscura");
        _pSuperficie ??= (Brush)FindResource("Superficie");
        _pVermelho ??= (Brush)FindResource("Vermelho");
        _pLinha ??= new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0));
        if (_pLinha.CanFreeze) _pLinha.Freeze();
        _sBotaoCategoria ??= (Style)FindResource("BotaoCategoria");
        _sBotaoRedondo ??= (Style)FindResource("BotaoRedondo");
        _sCampo ??= (Style)FindResource("CampoQuantidade");
    }

    private void MontarLista()
    {
        ResolverRecursos();
        ListaCategorias.Children.Clear();
        // Agrupado preservando a ordem da consulta (categoria, nome), igual ao
        // groupBy do app web.
        foreach (var grupo in _produtos.GroupBy(p => p.Categoria))
        {
            ListaCategorias.Children.Add(MontarCategoria(grupo.Key, [.. grupo]));
        }
    }

    /// <summary>
    /// Monta o card da categoria com o cabeçalho pronto e o conteúdo VAZIO.
    /// As linhas nascem na primeira vez que a categoria é aberta, e a partir
    /// daí abrir/fechar só troca Visibility.
    ///
    /// Antes, cada clique chamava MontarLista() e reconstruía a lista INTEIRA —
    /// todas as categorias, todas as linhas, inclusive as que nem estavam na
    /// tela. Num caixa fraco isso é o travamento que aparecia ao abrir uma
    /// categoria e ao rolar. O React não sofria disso porque só remonta o que
    /// mudou; aqui a reconstrução era explícita e minha.
    /// </summary>
    private Border MontarCategoria(string categoria, List<ProdutoVitrine> itens)
    {
        var aberta = _categoriasAbertas.Contains(categoria);

        // Colunas * + Auto: o nome da categoria cede espaço e corta com
        // reticências; a contagem e a seta mantêm o tamanho. Sem isso os dois
        // ocupavam a MESMA célula e se sobrepunham em janela estreita.
        var cabecalhoInterno = new Grid();
        cabecalhoInterno.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cabecalhoInterno.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nomeCategoria = new TextBlock
        {
            Text = categoria,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(nomeCategoria, 0);
        cabecalhoInterno.Children.Add(nomeCategoria);

        var seta = new TextBlock
        {
            Text = aberta ? "▲" : "▼",
            FontSize = 10,
            Foreground = _pMarcaEscura,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var direita = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        direita.Children.Add(new TextBlock
        {
            Text = $"{itens.Count} {(itens.Count == 1 ? "item" : "itens")}",
            FontSize = 13,
            Foreground = _pTintaFraca,
            VerticalAlignment = VerticalAlignment.Center,
        });
        direita.Children.Add(seta);
        Grid.SetColumn(direita, 1);
        cabecalhoInterno.Children.Add(direita);

        var conteudo = new StackPanel();
        var caixaConteudo = new Border
        {
            BorderBrush = _pLinha,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 0, 20, 0),
            Child = conteudo,
            Visibility = aberta ? Visibility.Visible : Visibility.Collapsed,
        };

        void PreencherSePreciso()
        {
            if (conteudo.Children.Count > 0) return;   // já montado antes
            for (var i = 0; i < itens.Count; i++)
            {
                if (i > 0) conteudo.Children.Add(new Border { Height = 1, Background = _pLinha });
                conteudo.Children.Add(MontarLinha(itens[i]));
            }
        }

        if (aberta) PreencherSePreciso();

        var botao = new Button { Style = _sBotaoCategoria, Content = cabecalhoInterno };
        botao.Click += (_, _) =>
        {
            var abrindo = !_categoriasAbertas.Contains(categoria);
            if (abrindo) { _categoriasAbertas.Add(categoria); PreencherSePreciso(); }
            else _categoriasAbertas.Remove(categoria);

            caixaConteudo.Visibility = abrindo ? Visibility.Visible : Visibility.Collapsed;
            seta.Text = abrindo ? "▲" : "▼";
        };

        var corpo = new StackPanel();
        corpo.Children.Add(botao);
        corpo.Children.Add(caixaConteudo);

        return new Border
        {
            Background = _pSuperficie,
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(0, 0, 0, 12),
            Child = corpo,
            ClipToBounds = true,
        };
    }

    private Grid MontarLinha(ProdutoVitrine p)
    {
        var linha = new Grid { Margin = new Thickness(0, 14, 0, 14) };
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var esquerda = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        esquerda.Children.Add(new TextBlock
        {
            Text = p.Nome,
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var rotuloPendente = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = _pMarcaEscura,
            Visibility = Visibility.Collapsed,
        };
        esquerda.Children.Add(rotuloPendente);
        esquerda.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(esquerda, 0);
        linha.Children.Add(esquerda);

        var campo = new TextBox { Style = _sCampo, IsEnabled = _diaAberto, Margin = new Thickness(8, 0, 8, 0) };
        var botaoMenos = new Button { Style = _sBotaoRedondo, Content = "−" };
        var botaoMais = new Button { Style = _sBotaoRedondo, Content = "+", IsEnabled = _diaAberto };

        var aviso = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = _pVermelho,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 240,
            Visibility = Visibility.Collapsed,
        };

        DispatcherTimer? timerAviso = null;
        void Avisar(string texto)
        {
            aviso.Text = texto;
            aviso.Visibility = Visibility.Visible;
            timerAviso?.Stop();
            // 4 segundos, igual ao aviso da versão Electron.
            timerAviso = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timerAviso.Tick += (s, _) =>
            {
                aviso.Visibility = Visibility.Collapsed;
                ((DispatcherTimer)s!).Stop();
            };
            timerAviso.Start();
        }

        void Pintar()
        {
            campo.Text = Formatar(p.Total);
            botaoMenos.IsEnabled = _diaAberto && p.Pendente > 0;
            rotuloPendente.Text = $"+{Formatar(p.Pendente)} a salvar";
            rotuloPendente.Visibility = p.Pendente > 0 ? Visibility.Visible : Visibility.Collapsed;
            AtualizarBarraSalvar();
        }

        // Seleciona tudo ao focar: tocar no campo e digitar substitui, em vez de
        // acrescentar ao lado (mesma correção feita na versão Electron).
        campo.GotKeyboardFocus += (_, _) => campo.SelectAll();
        campo.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!campo.IsKeyboardFocusWithin) { e.Handled = true; campo.Focus(); }
        };
        campo.PreviewTextInput += (_, e) =>
        {
            if (!e.Text.All(char.IsDigit)) e.Handled = true;
        };
        campo.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;

            // Não precisa gravar à mão: mudar o foco dispara o LostFocus deste
            // campo, que é quem aplica o valor digitado e a trava do saldo.
            FocarProximoCampo(campo);
        };
        campo.LostFocus += (_, _) =>
        {
            var texto = campo.Text.Trim();
            var alvo = decimal.TryParse(texto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : p.QuantidadeSalva;
            // A trava: nunca abaixo do que já está salvo no banco.
            if (alvo < p.QuantidadeSalva)
                Avisar($"Não dá para reduzir: já há {Formatar(p.QuantidadeSalva)} em estoque.");
            p.Pendente = Math.Max(p.QuantidadeSalva, alvo) - p.QuantidadeSalva;
            Pintar();
        };

        botaoMais.Click += (_, _) => { p.Pendente += 1; Pintar(); };
        botaoMenos.Click += (_, _) => { p.Pendente = Math.Max(0, p.Pendente - 1); Pintar(); };

        var controles = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        // Espaçamento por Margin no campo, e não por Borders vazias: são dois
        // elementos visuais a menos por linha, e a lista tem dezenas delas.
        controles.Children.Add(botaoMenos);
        controles.Children.Add(campo);
        controles.Children.Add(botaoMais);

        var direita = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        direita.Children.Add(controles);
        direita.Children.Add(aviso);
        Grid.SetColumn(direita, 1);
        linha.Children.Add(direita);

        Pintar();
        return linha;
    }

    private static string Formatar(decimal v) =>
        v == Math.Floor(v)
            ? ((long)v).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.##", CultureInfo.InvariantCulture);

    private void AtualizarBarraSalvar()
    {
        var tem = _produtos.Any(p => p.Pendente > 0);
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
        var pendentes = _produtos.Where(p => p.Pendente > 0).ToList();
        var falhas = new List<string>();
        foreach (var p in pendentes)
        {
            try { await _dados.AdicionarAsync(p.Codfic, p.Pendente, _operador.Codusu); }
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
    /// <summary>
    /// Leva o foco para o próximo campo de quantidade, na ordem da tela.
    ///
    /// Digitar a quantidade e apertar Enter para cair no item de baixo é o que
    /// torna viável lançar a vitrine inteira sem tirar a mão do teclado.
    /// </summary>
    private void FocarProximoCampo(TextBox atual)
    {
        var campos = CamposDeQuantidade();
        var atualNaLista = campos.IndexOf(atual);

        if (atualNaLista < 0 || atualNaLista + 1 >= campos.Count)
        {
            // Último campo à vista: fica onde está, com o texto selecionado.
            // Pular para o começo faria o operador perder o lugar sem perceber.
            atual.SelectAll();
            return;
        }

        var proximo = campos[atualNaLista + 1];
        proximo.Focus();

        // A lista é rolável e o próximo item pode estar fora da janela — sem
        // isto o foco iria para um campo que ninguém está vendo.
        proximo.BringIntoView();
    }

    /// <summary>
    /// Os campos de quantidade na ordem em que aparecem na tela.
    ///
    /// Percorre a árvore visual em vez de guardar uma lista paralela: as linhas
    /// nascem quando a categoria é aberta pela primeira vez, então uma lista em
    /// ordem de criação não seria a ordem da tela. Categoria fechada fica de
    /// fora sozinha, porque o conteúdo dela é Collapsed e o IsVisible dos
    /// filhos vira false.
    /// </summary>
    private List<TextBox> CamposDeQuantidade() =>
        [.. Descendentes(ListaCategorias)
            .OfType<TextBox>()
            .Where(c => c.Style == _sCampo && c.IsVisible && c.IsEnabled)];

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

}
