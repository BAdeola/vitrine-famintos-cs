using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace YellowVitrine.Desktop;

public sealed record ProdutoVitrine(int Codfic, string Nome, decimal QuantidadeSalva, string Categoria)
{
    /// <summary>Quanto a pessoa acrescentou nesta sessão e ainda não salvou. Nunca negativo.</summary>
    public decimal Pendente { get; set; }
    public decimal Total => QuantidadeSalva + Pendente;
}

public sealed record Operador(int Codusu, string Apelido);

/// <summary>
/// Acesso ao OCSFF. Porta fiel do backend Node (backend/src/modules/*), inclusive
/// nas travas: o objetivo é que as duas versões se comportem igual enquanto
/// estiverem convivendo, senão não dá pra comparar uma com a outra.
/// </summary>
public sealed class Dados(string connectionString)
{
    private readonly string _cs = connectionString;

    // O esquema do OCSFF é legado (veio do COBOL) e as colunas numéricas
    // aparecem ora como numeric/decimal, ora como int. GetDecimal/GetInt32
    // exigem o tipo EXATO e lançam InvalidCastException quando erram — o
    // driver do Node convertia sozinho, por isso a versão antiga nunca
    // reclamou. Estes helpers convertem a partir do valor bruto.
    private static decimal Dec(object? v) => v is null or DBNull ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
    private static int Int(object? v) => v is null or DBNull ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture);
    private static string Txt(object? v) => v is null or DBNull ? "" : v.ToString()!.TrimEnd();

    private async Task<SqlConnection> AbrirAsync()
    {
        var conn = new SqlConnection(_cs);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>
    /// Mesma regra do resto do sistema (COBOL): existir QUALQUER linha em
    /// controle_caixa significa dia aberto. O valor de situac não importa — a
    /// tabela só esvazia no fechamento geral do dia.
    /// </summary>
    public async Task<bool> DiaAbertoAsync()
    {
        await using var conn = await AbrirAsync();
        await using var cmd = new SqlCommand("SELECT TOP 1 situac FROM controle_caixa", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync();
    }

    /// <summary>
    /// Quem abriu o turno mais recente ainda sem fechamento. É o autor gravado
    /// em logest_vitrine.codusu desde que o login próprio do app foi removido.
    /// </summary>
    public async Task<Operador?> ResponsavelTurnoAbertoAsync()
    {
        const string sql = """
            SELECT TOP 1 a.responsavel AS codusu, RTRIM(ISNULL(u.apelid, '')) AS apelido
            FROM abetur a
            LEFT JOIN cadusu u ON u.codusu = a.responsavel
            WHERE NOT EXISTS (
              SELECT 1 FROM fectur f WHERE f.numcai = a.numcai AND f.sequencia_dia = a.sequencia_dia
            )
            ORDER BY a.data_abertura DESC
            """;
        await using var conn = await AbrirAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        var apelido = Txt(r.GetValue(1));
        return new Operador(Int(r.GetValue(0)), string.IsNullOrWhiteSpace(apelido) ? "CAIXA" : apelido);
    }

    /// <summary>
    /// Produtos da tela: tudo em cadest_vitrine cuja ficha está marcada
    /// vitrine = 1 e ativa. Sem grupo correspondente cai em "SEM CATEGORIA"
    /// em vez de sumir da lista.
    /// </summary>
    public async Task<List<ProdutoVitrine>> ListarAsync()
    {
        const string sql = """
            SELECT c.codfic, RTRIM(f.nomfic) AS nome, c.quantidade,
                   RTRIM(ISNULL(g.nomgru, 'SEM CATEGORIA')) AS categoria
            FROM cadest_vitrine c
            JOIN fictec f ON f.codfic = c.codfic
            LEFT JOIN grufic g ON g.codgru = f.codgru
            WHERE f.vitrine = 1 AND RTRIM(f.situac) = 'ATIVO'
            ORDER BY categoria, nome
            """;
        var lista = new List<ProdutoVitrine>();
        await using var conn = await AbrirAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            lista.Add(new ProdutoVitrine(
                Int(r.GetValue(0)),
                Txt(r.GetValue(1)),
                Dec(r.GetValue(2)),
                Txt(r.GetValue(3))));
        }
        return lista;
    }

    /// <summary>
    /// Soma <paramref name="delta"/> ao estoque e grava a linha de auditoria,
    /// atomicamente. O UPDLOCK/HOLDLOCK na leitura mais um UPDATE que só
    /// SOMA é o que garante "nunca reduzir" no próprio banco, não só na tela.
    ///
    /// logest_vitrine.id não é IDENTITY, então o próximo valor sai de um
    /// MAX(id) sob TABLOCKX dentro da mesma transação — sem isso, dois caixas
    /// salvando junto colidiriam no mesmo id.
    /// </summary>
    public async Task AdicionarAsync(int codfic, decimal delta, int codusu)
    {
        if (delta <= 0) throw new InvalidOperationException("A quantidade adicionada precisa ser maior que zero.");

        await using var conn = await AbrirAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            // Rechecado DENTRO da transação: cobre o dia fechar no meio de um save.
            await using (var cmdTurno = new SqlCommand("SELECT TOP 1 situac FROM controle_caixa", conn, tx))
            await using (var rTurno = await cmdTurno.ExecuteReaderAsync())
            {
                if (!await rTurno.ReadAsync())
                    throw new InvalidOperationException("O dia está fechado. Abra um turno para atualizar a vitrine.");
            }

            decimal anterior;
            string nome;
            const string sqlLer = """
                SELECT c.quantidade, RTRIM(f.nomfic) AS nome
                FROM cadest_vitrine c WITH (UPDLOCK, HOLDLOCK)
                JOIN fictec f ON f.codfic = c.codfic
                WHERE c.codfic = @codfic
                """;
            await using (var cmdLer = new SqlCommand(sqlLer, conn, tx))
            {
                cmdLer.Parameters.Add("@codfic", SqlDbType.Decimal).Value = codfic;
                await using var rLer = await cmdLer.ExecuteReaderAsync();
                if (!await rLer.ReadAsync())
                    throw new InvalidOperationException("Produto não encontrado na vitrine.");
                anterior = Dec(rLer.GetValue(0));
                nome = Txt(rLer.GetValue(1));
            }

            var atual = anterior + delta;

            await using (var cmdUpd = new SqlCommand(
                "UPDATE cadest_vitrine SET quantidade = @quantidade WHERE codfic = @codfic", conn, tx))
            {
                cmdUpd.Parameters.Add("@quantidade", SqlDbType.Decimal).Value = atual;
                cmdUpd.Parameters.Add("@codfic", SqlDbType.Decimal).Value = codfic;
                await cmdUpd.ExecuteNonQueryAsync();
            }

            decimal proximoId;
            await using (var cmdId = new SqlCommand(
                "SELECT ISNULL(MAX(id), 0) + 1 AS nextId FROM logest_vitrine WITH (TABLOCKX, HOLDLOCK)", conn, tx))
            {
                proximoId = Dec(await cmdId.ExecuteScalarAsync());
            }

            const string sqlLog = """
                INSERT INTO logest_vitrine (id, data, codfic, nomfic, qtd_anterior, quantidade, qtd_atual, codusu)
                VALUES (@id, GETDATE(), @codfic, @nomfic, @qtdAnterior, @quantidade, @qtdAtual, @codusu)
                """;
            await using (var cmdLog = new SqlCommand(sqlLog, conn, tx))
            {
                cmdLog.Parameters.Add("@id", SqlDbType.Decimal).Value = proximoId;
                cmdLog.Parameters.Add("@codfic", SqlDbType.Decimal).Value = codfic;
                cmdLog.Parameters.Add("@nomfic", SqlDbType.Char, 40).Value = nome;
                cmdLog.Parameters.Add("@qtdAnterior", SqlDbType.Decimal).Value = anterior;
                cmdLog.Parameters.Add("@quantidade", SqlDbType.Decimal).Value = delta;
                cmdLog.Parameters.Add("@qtdAtual", SqlDbType.Decimal).Value = atual;
                cmdLog.Parameters.Add("@codusu", SqlDbType.Decimal).Value = codusu;
                await cmdLog.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }
        catch
        {
            try { await tx.RollbackAsync(); } catch { /* pode já ter caído sozinha */ }
            throw;
        }
    }
}
