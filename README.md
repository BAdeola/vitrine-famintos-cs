# Vitrine Famintos — versão C#

Tela de **atualização do estoque de vitrine** do caixa (OCSFF). Aplicativo
nativo Windows (WPF, .NET 10) que fala direto com o SQL Server, chamado pelo
`menu_caixa` (COBOL).

Substitui a versão anterior em Electron ([yellow-cadest-vitrine](https://github.com/BAdeola/yellow-cadest-vitrine)).

## Por que foi reescrito

No PC do caixa — 4 GB de RAM, processador fraco, já rodando o SQL Server e o
COBOL — a versão Electron levava **cerca de 11 segundos** para abrir. Esse é o
piso do Chromium naquela máquina: subir o processo principal, o de GPU, o
renderer e os utilitários. Não cede a otimização; tentamos, inclusive
eliminando um processo Node inteiro do caminho, e o tempo não mudou.

| | Electron | C# (este) |
|---|---|---|
| Processos | Chromium (4+) + Node do backend | **1** |
| Caminho até o banco | tela → HTTP → Node → SQL Server | **tela → SQL Server** |
| Runtime a instalar | Node.js | **nenhum** (self-contained) |
| Abrir (frio) | ~11 s | ~4 s |
| Abrir (residente) | — | **~0,2 s** |

## Como funciona

O programa fica **residente na bandeja** do Windows. Sobe uma vez no logon e
não sai mais; "abrir" deixa de ser iniciar um processo e passa a ser mostrar
uma janela que já existe.

- **No logon**: atalho no Startup rodando `"Yellow Vitrine.exe" --tray` — sobe
  escondido, já montado.
- **No menu_caixa**: o COBOL chama o **mesmo `.exe`**, sem argumento. Essa
  segunda instância percebe que já há uma rodando (Mutex nomeado), sinaliza
  para ela aparecer (EventWaitHandle nomeado) e encerra. O COBOL não precisa
  saber de nada disso.
- **Botão Fechar**: esconde, não encerra.
- **Encerra de verdade**: menu da bandeja → Sair, ou quando o Windows
  desliga/desloga.

Se nenhuma instância estiver de pé (depois de uma queda, por exemplo), a
chamada do COBOL vira a instância normal e abre a janela. Não existe estado
ruim para tratar.

## A tela

Um card por produto, todos soltos numa grade que reflui conforme a largura da
janela. Cada card tem a foto, o nome e o campo de quantidade — digitável, sem
os botões `+` e `−`.

Não há agrupamento por categoria. Até 2026-09-24 a lista era uma sanfona por
grupo; hoje os produtos aparecem todos, em sequência.

### Por que a lista é virtualizada

Tirar a sanfona significa que todos os produtos passam a existir ao mesmo
tempo — e com foto. Era justamente a montagem de tudo de uma vez que travava a
máquina do caixa.

O `VirtualizingStackPanel` do WPF só trabalha em uma direção, e um
`WrapPanel` comum não virtualiza nada. Por isso os produtos são agrupados em
**faixas** horizontais antes de entrar na lista, e a virtualização acontece por
faixa. Quantos cards cabem por faixa é recalculado quando a largura muda — e só
quando o número muda de fato, senão arrastar a borda da janela remontaria a
lista a cada pixel.

Medido com 130 produtos numa janela de 980x620: **12 cards montados**, não 130.

### O topo tem só o título e o Fechar

Nada de tarjas permanentes. O que era faixa passou para o próprio título:

| Situação | Título |
|---|---|
| normal | Manutenção Vitrine |
| modo homologação ligado | Manutenção Vitrine · homologação |
| dia fechado | Manutenção Vitrine · dia fechado |

A faixa de **erro** continua, porque não é texto fixo: só aparece quando uma
gravação falha, e é a única forma de saber que algo não passou.

## Lançar pelo teclado

No campo de quantidade, **Enter salta para o item de baixo**, já com o texto
selecionado — digitar substitui, não acrescenta ao lado. É o que torna viável
lançar a vitrine inteira sem tirar a mão do teclado.

O salto segue a ordem dos produtos, não a da tela: com a lista virtualizada, o
card seguinte pode nem existir ainda na árvore visual. O programa rola até ele,
espera o layout e só então põe o foco. No último item o foco fica onde está —
voltar ao começo faria o operador perder o lugar sem perceber.

Mudar o foco é o que grava o valor: o mesmo `LostFocus` que já aplicava a
trava do saldo.

## Modo homologação

Para testar sem digitar item por item. No `appsettings.json`:

```json
"ModoHomologacao": true,
"QuantidadeDeHomologacao": 100
```

Com isso, todo item de vitrine **zerado** já abre com 100 pendente — basta
salvar uma vez e vale para todos, inclusive os que estão fora da área visível
(o Salvar percorre os produtos, não a tela — o que importa com a lista
virtualizada, onde a maioria dos cards nem existe na árvore).

**Só os zerados.** Quem já tem saldo mantém o que tem: somar em cima inflaria
um número real. Na prática a vitrine zera no fechamento do dia, que é
justamente quando isto serve.

Nada é gravado direto: o valor entra como **pendente** e passa pelo mesmo
Salvar, com as mesmas travas. Depois de salvar, os itens deixam de estar
zerados, então recarregar não empilha outro 100 por cima.

Enquanto o modo está ligado, a tela mostra um aviso vermelho permanente. Não é
decoração: a gravação vai para o estoque de verdade, e deixado ligado sem
querer alguém salva 100 de tudo achando que é o comportamento normal.

Duas coisas que valem saber:

- **Ausente é desligado.** Um `appsettings.json` de instalação anterior não
  tem essas chaves, e atualizar o programa nunca liga o modo sozinho.
- **`true` sem aspas.** `"true"` entre aspas é texto, não booleano, e o modo
  fica desligado. O sinal é o aviso vermelho não aparecer.

## Regras de negócio

Portadas fielmente do backend Node da versão anterior. As três valem ao mesmo
tempo, em camadas independentes.

### Alteração livre, sem ficar negativo

A tela altera o saldo para mais **e para menos**, a qualquer momento. Até
2026-09-24 valia o oposto — "nunca reduzir, só acrescentar" — em três camadas;
a regra foi removida a pedido, e ficou só o piso:

| Onde | Como |
|---|---|
| Campo de quantidade | valor negativo vira zero, no setter de `Total` — o único ponto por onde toda digitação passa |
| Banco | recusa o ajuste se o saldo ficaria negativo |

#### O ajuste é relativo, não absoluto

O que vai para o banco é o **delta** (`quantidade + @delta`), não o número que
aparece na tela. Isso importa na redução: entre o operador ver a tela e clicar
em salvar, uma venda pode ter baixado o saldo. Somando o ajuste, essa venda é
preservada; gravando o valor absoluto, ela sumiria sem deixar rastro.

Se mesmo assim o resultado ficaria negativo, a gravação **falha para aquele
item** e a tela mostra o motivo com o saldo novo — em vez de gravar um número
que ninguém pediu. Os demais itens são salvos normalmente.

O método no `Dados.cs` chama-se `AjustarAsync`. Era `AdicionarAsync` enquanto
só somava.

### Dia aberto

Existir **qualquer** linha em `controle_caixa` significa dia aberto — mesma
regra do resto do sistema (COBOL). O valor de `situac` não importa: a tabela
só é esvaziada no fechamento geral do dia. Rechecado dentro da transação do
save, para cobrir o dia fechar no meio de uma gravação.

### Autoria

`logest_vitrine.codusu` recebe quem abriu o turno mais recente ainda sem
fechamento (`abetur` sem `fectur` correspondente). O app não tem login
próprio — foi removido em 2026-09 sem perder o rastreio de quem mexeu.

### Id do log

`logest_vitrine.id` não é `IDENTITY`. O próximo valor sai de um `MAX(id) + 1`
sob `TABLOCKX` **dentro da mesma transação** — sem isso, dois caixas salvando
ao mesmo tempo colidiriam no mesmo id.

## Tabelas usadas (banco OCSFF)

| Tabela | Para quê |
|---|---|
| `cadest_vitrine` | o estoque em si (`codfic`, `quantidade`) |
| `fictec` | nome do produto, e os filtros `vitrine = 1` e `situac = 'ATIVO'` |
| `grufic` | categoria (produto sem grupo cai em "SEM CATEGORIA") |
| `logest_vitrine` | auditoria de cada alteração |
| `controle_caixa` | dia aberto/fechado |
| `abetur` / `fectur` / `cadusu` | quem está no turno |

## Estrutura

```
src/YellowVitrine.Desktop/
  App.xaml / App.xaml.cs      bandeja, instância única, ciclo de vida
  MainWindow.xaml / .xaml.cs  a tela
  Dados.cs                    acesso ao SQL Server (todas as queries)
  appsettings.json            conexão (o do repositório tem placeholder)
```

## Compilar

Precisa do SDK do .NET 10.

```
cd src/YellowVitrine.Desktop
dotnet publish -c Release -o ../../publish
```

Sai em `publish/` — ~140 MB, self-contained (não exige runtime .NET na
máquina de destino) e com ReadyToRun, que é o que derruba o tempo de partida.

## Instalar

Ver **[MANUAL-INSTALACAO.md](MANUAL-INSTALACAO.md)** — passo a passo, incluindo
como atualizar sem perder a senha do banco.

## Decisões registradas

- **Sem modo escuro.** A versão Electron tinha alternador; aqui ficou só o
  tema claro, por decisão.
- **Sem WebView2.** Seria trazer o Chromium de volta e perder o ganho inteiro.
- **WPF e não WinForms.** Os cards arredondados, o amarelo da marca e as
  seções recolhíveis exigem estilização real.
- **Acesso ao celular fora de escopo.** A versão Electron servia a tela na
  rede local; isso saiu em 2026-09-17.
