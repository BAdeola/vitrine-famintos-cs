using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace YellowVitrine.Desktop;

/// <summary>
/// Traz as fotos dos produtos do repositório central (a mesma VPS que abastece
/// o totem) para uma pasta local no caixa.
///
/// O protocolo é o mesmo que o totem usa, de propósito: <c>/manifest</c> lista
/// os arquivos com tamanho e data, <c>/files/products/&lt;arquivo&gt;</c> baixa
/// um. Inventar um caminho próprio significaria manter dois contratos contra o
/// mesmo servidor.
///
/// A diferença para o totem é o recorte: aqui só interessam as fotos dos
/// produtos que estão na vitrine, não o catálogo inteiro.
/// </summary>
public sealed class ImagensDaVitrine(string urlDoRepositorio, string pastaLocal)
{
    // Um cliente só para o processo: criar um HttpClient por chamada esgota as
    // portas do Windows quando são dezenas de downloads seguidos.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _url = urlDoRepositorio.TrimEnd('/');

    public string PastaLocal { get; } = pastaLocal;

    public sealed record Resultado(int Baixados, int JaTinha, int Falharam, string? Erro);

    private sealed record ItemDoManifesto(string arquivo, string modificadoEm, long tamanho);

    /// <summary>Caminho da foto se ela já estiver no disco; null se não.</summary>
    public string? CaminhoSeExistir(string arquivo)
    {
        if (string.IsNullOrWhiteSpace(arquivo)) return null;
        var caminho = Path.Combine(PastaLocal, arquivo);
        return File.Exists(caminho) ? caminho : null;
    }

    /// <summary>
    /// Baixa as fotos que faltam ou mudaram.
    ///
    /// <paramref name="aoChegar"/> é chamado a cada arquivo pronto, para a tela
    /// ir mostrando as fotos conforme chegam em vez de esperar o lote inteiro.
    /// </summary>
    public async Task<Resultado> SincronizarAsync(
        IReadOnlyCollection<string> arquivos,
        Action<string, string>? aoChegar = null,
        CancellationToken ct = default)
    {
        var necessarios = arquivos
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (necessarios.Count == 0) return new Resultado(0, 0, 0, null);

        Directory.CreateDirectory(PastaLocal);

        List<ItemDoManifesto> doRepositorio;
        try
        {
            using var resposta = await Http.GetAsync($"{_url}/manifest", ct);
            resposta.EnsureSuccessStatusCode();

            using var fluxo = await resposta.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(fluxo, cancellationToken: ct);

            doRepositorio = doc.RootElement.TryGetProperty("products", out var produtos)
                ? [.. produtos.EnumerateArray().Select(e => new ItemDoManifesto(
                    e.GetProperty("arquivo").GetString() ?? "",
                    e.TryGetProperty("modificadoEm", out var m) ? m.GetString() ?? "" : "",
                    e.TryGetProperty("tamanho", out var s) ? s.GetInt64() : 0))]
                : [];
        }
        catch (Exception e)
        {
            // Repositório fora do ar não pode impedir a vitrine de funcionar: o
            // operador precisa lançar quantidade, e a foto é ajuda visual. O que
            // já está em disco continua valendo.
            return new Resultado(0, 0, 0, $"{e.GetType().Name} — {e.Message}");
        }

        var baixados = 0;
        var jaTinha = 0;
        var falharam = 0;

        foreach (var item in doRepositorio.Where(i => necessarios.Contains(i.arquivo)))
        {
            ct.ThrowIfCancellationRequested();

            var destino = Path.Combine(PastaLocal, item.arquivo);
            if (!PrecisaBaixar(destino, item))
            {
                jaTinha++;
                aoChegar?.Invoke(item.arquivo, destino);
                continue;
            }

            try
            {
                await BaixarAsync(item.arquivo, destino, ct);
                baixados++;
                aoChegar?.Invoke(item.arquivo, destino);
            }
            catch (OperationCanceledException) { throw; }
            catch { falharam++; }
        }

        return new Resultado(baixados, jaTinha, falharam, null);
    }

    /// <summary>
    /// Mesma regra do totem: não existe, tamanho diferente, ou o remoto é mais
    /// novo. Sem estado próprio — o disco já é a fonte da verdade.
    /// </summary>
    private static bool PrecisaBaixar(string caminhoLocal, ItemDoManifesto item)
    {
        var arquivo = new FileInfo(caminhoLocal);
        if (!arquivo.Exists) return true;
        if (item.tamanho > 0 && arquivo.Length != item.tamanho) return true;

        return DateTime.TryParse(item.modificadoEm, null,
                   System.Globalization.DateTimeStyles.AdjustToUniversal, out var remoto)
               && remoto > arquivo.LastWriteTimeUtc;
    }

    /// <summary>
    /// Baixa para um .tmp ao lado e só então troca o nome.
    ///
    /// Sem isso, uma queda no meio do download deixaria um arquivo pela metade
    /// com o nome final — e ele passaria na checagem de "já existe" para
    /// sempre, com a foto quebrada no card.
    /// </summary>
    private async Task BaixarAsync(string arquivo, string destino, CancellationToken ct)
    {
        var temporario = destino + ".tmp";

        using (var resposta = await Http.GetAsync($"{_url}/files/products/{Uri.EscapeDataString(arquivo)}",
                   HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resposta.EnsureSuccessStatusCode();
            await using var origem = await resposta.Content.ReadAsStreamAsync(ct);
            await using var gravando = File.Create(temporario);
            await origem.CopyToAsync(gravando, ct);
        }

        File.Move(temporario, destino, overwrite: true);
    }
}
