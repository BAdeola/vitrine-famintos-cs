# Manual de instalação — Vitrine Famintos (C#)

Tudo aqui é executado **no PC do caixa**, no PowerShell. Os caminhos usam
`C:\YellowVitrineCS` como exemplo; se instalar em outro lugar, ajuste.

---

## 1. Primeira instalação

### 1.1 Gerar o pacote

Numa máquina com o SDK do .NET 10 (não precisa ser o caixa):

```powershell
cd src\YellowVitrine.Desktop
dotnet publish -c Release -o ..\..\publish
```

Leve a pasta `publish` inteira até o caixa — pen drive, rede, TeamViewer. São
~140 MB e cerca de 280 arquivos. **Copiar só o `.exe` não funciona**: ele tem
162 KB, todo o resto é o runtime .NET e o WPF que vão junto.

### 1.2 Colocar no lugar

Crie `C:\YellowVitrineCS` e copie o conteúdo de `publish` para lá. No final
tem que existir:

```
C:\YellowVitrineCS\Yellow Vitrine.exe
C:\YellowVitrineCS\appsettings.json
```

### 1.3 Configurar o banco

Abra `C:\YellowVitrineCS\appsettings.json` e troque `TROQUE_AQUI` pela senha
real:

```json
{
  "ConnectionString": "Server=localhost,1433;Database=OCSFF;User Id=sa;Password=TROQUE_AQUI;TrustServerCertificate=True;Encrypt=False;Connect Timeout=15"
}
```

**Atenção ao formato**: servidor e porta vão juntos, separados por **vírgula**
(`localhost,1433`), não por dois-pontos.

Confira se conectou, sem depender do app:

```powershell
$cs = (Get-Content 'C:\YellowVitrineCS\appsettings.json' -Raw | ConvertFrom-Json).ConnectionString
$c = New-Object System.Data.SqlClient.SqlConnection $cs
try { $c.Open(); Write-Output "CONECTOU — banco: $($c.Database)"; $c.Close() } catch { Write-Output "FALHOU: $($_.Exception.Message)" }
```

### 1.4 Fazer subir junto com o Windows

É isso que torna a abertura instantânea: o programa sobe uma vez, no logon, e
fica na bandeja.

```powershell
$destino = 'C:\YellowVitrineCS\Yellow Vitrine.exe'
$lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'YellowVitrineCS.lnk'
$s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$s.TargetPath = $destino
$s.Arguments = '--tray'
$s.WorkingDirectory = Split-Path $destino
$s.Description = 'Vitrine Famintos (residente na bandeja)'
$s.Save()
Write-Output "atalho criado: $lnk"
```

O `--tray` é obrigatório. Sem ele o programa abre a janela no logon, em vez de
ficar escondido.

Teste sem reiniciar:

```powershell
Start-Process (Join-Path ([Environment]::GetFolderPath('Startup')) 'YellowVitrineCS.lnk')
```

Deve aparecer um **círculo amarelo na bandeja** e **nenhuma janela**.

### 1.5 Apontar o menu_caixa

O COBOL chama o executável direto:

```
C:\YellowVitrineCS\Yellow Vitrine.exe
```

Sem argumento nenhum. Não precisa de `.bat` nem de arquivo intermediário.

---

## 2. Atualizar para uma versão nova

A ordem importa, senão você perde a senha do banco.

**1.** Traga a `publish` nova para um lugar **temporário** no caixa, por
exemplo `C:\temp\publish`. Não copie direto por cima ainda.

**2.** Guarde uma cópia da configuração:

```powershell
Copy-Item 'C:\YellowVitrineCS\appsettings.json' 'C:\appsettings-backup.json' -Force
```

**3.** Pare o programa (senão os arquivos ficam travados):

```powershell
Get-Process 'Yellow Vitrine' -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 2
```

**4.** Copie por cima **preservando o `appsettings.json`**:

```powershell
robocopy 'C:\temp\publish' 'C:\YellowVitrineCS' /E /XF appsettings.json
```

O `/E` copia tudo recursivamente; o `/XF appsettings.json` exclui esse único
arquivo da cópia. É ele que salva sua senha.

**5.** Confirme que a senha continua lá:

```powershell
(Get-Content 'C:\YellowVitrineCS\appsettings.json' -Raw | ConvertFrom-Json).ConnectionString -replace 'Password=[^;]*','Password=***'
```

Se aparecer `TROQUE_AQUI`, restaure com o backup do passo 2.

**6.** Suba de novo:

```powershell
Start-Process (Join-Path ([Environment]::GetFolderPath('Startup')) 'YellowVitrineCS.lnk')
```

---

## 3. Problemas conhecidos

### "Falha ao carregar os dados: ..."

A faixa vermelha traz o **tipo e o texto reais** da exceção. Use-os: a
mensagem não rotula nada de "erro de conexão", justamente porque o mesmo
ponto cobre a conexão e as consultas, e rotular errado manda investigar o
lugar errado.

Se o teste de conexão do item 1.3 funciona mas o app falha, o problema está
numa query, não na configuração.

### Editei o appsettings.json e não mudou nada

Só acontece em versões anteriores a 2026-09-18. A partir dela o arquivo é
relido toda vez que a janela aparece — corrigir e reabrir pelo menu já basta.

Em versões antigas, era preciso encerrar pela bandeja e subir de novo.

### O programa abre, mas demora vários segundos

O residente não está de pé, e cada chamada está pagando a partida a frio.
Confira:

```powershell
Get-Process 'Yellow Vitrine' -ErrorAction SilentlyContinue | Select-Object Id,StartTime,Path | Format-Table -AutoSize
```

- **Sem nenhum processo** → o atalho do Startup não rodou. Veja o item 1.4.
- **O `Id` muda a cada chamada** → o sinal não está chegando e cada chamada
  cria um processo novo.
- **O `Path` aponta para outra pasta** → o menu_caixa está chamando o programa
  errado (a versão Electron antiga, por exemplo).

### Abriu a tela errada

A versão Electron antiga tem um **ícone de lua** (tema escuro) ao lado do
botão Fechar, e o Fechar tem símbolo de energia. Esta versão não tem nenhum
dos dois. Se você vê a lua, está abrindo o programa antigo.

### "Não foi possível conectar ao servidor." (frase exata, com ponto final)

Essa mensagem **é da versão Electron**, não desta. Ela depende do backend Node
na porta 3001. Se ele foi desligado, ela para de funcionar — o que é esperado.

### A pasta não deixa ser apagada/substituída

O programa está rodando. Pare com o comando do passo 3 da atualização. Se
persistir, feche as janelas do Explorer abertas naquela pasta.

---

## 4. Desinstalar / voltar atrás

```powershell
Get-Process 'Yellow Vitrine' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) 'YellowVitrineCS.lnk') -ErrorAction SilentlyContinue
```

Depois apague `C:\YellowVitrineCS` e reaponte o menu_caixa para onde estava.

Para voltar à versão Electron também é preciso religar o backend Node dela —
sem ele, aquela versão não funciona.
