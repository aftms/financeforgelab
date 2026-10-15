# Migração e operação local

Executar os comandos na raiz do repositório. Usar uma pasta nova para cada exportação, simulação e execução de migração. As pastas e ficheiros privados dos exemplos estão excluídos do controlo de versões.

## Extrair a origem Access

Fechar a aplicação antiga para obter uma cópia consistente. O extrator copia o ficheiro original antes de o abrir pelo fornecedor Access, verifica SHA-256 e lê uma segunda cópia em modo de leitura.

```powershell
powershell.exe -NoProfile -File tools/Export-LegacyAccess.ps1 `
  -Source 'C:\caminho\historico.accdb' `
  -Destination '.private/legacy-export' `
  -DatasetId 'legacy-home-finance'
```

Manter o mesmo `DatasetId` para versões da mesma origem. Uma base independente exige uma identidade diferente. O formato legado suportado é descrito em `LegacyPackage.cs`; o extrator preserva as restantes tabelas no arquivo, sem as transformar em movimentos financeiros. Uma base Access com outro modelo exige adaptar e testar o mapeamento.

O pacote inclui o backup Access, esquema, registos e manifesto de hashes. Pode conter dados pessoais de outros módulos presentes na origem. Tabelas ligadas, campos complexos não suportados e tabelas sem a chave esperada exigem revisão antes da migração.

## Simular e rever

```powershell
dotnet run --project src/FinanceForgeLab.Migration -- simulate `
  --package .private/legacy-export --output .private/simulation-01

dotnet run --project src/FinanceForgeLab.Migration -- simulate `
  --package .private/legacy-export --output .private/simulation-repeat `
  --baseline .private/simulation-01/simulation.sqlite
```

O segundo comando trabalha numa cópia SQLite da primeira simulação. Uma repetição idêntica deve inserir zero entidades operacionais. Rever `validation-private.json` e `recovery-check.json`: contagens, montantes, relações, arquivo bruto, integridade e recuperação devem conferir.

Avisos sobre movimentos iguais, valores negativos ou agregados antigos não provocam eliminações ou correções automáticas. Tipos desconhecidos, referências inválidas, conversões inexatas, origem alterada sob a mesma chave ou destino editado bloqueiam a carga. Os relatórios detalhados contêm valores financeiros e devem permanecer privados.

A ferramenta atual importa valores confirmados em EUR. Confirmar a moeda e a convenção de sinais da origem antes de executar a carga; outras moedas exigem adaptar a configuração e os testes.

## Criar a base operacional

Após rever o resultado da simulação:

```powershell
dotnet run --project src/FinanceForgeLab.Migration -- apply `
  --package .private/legacy-export --output .private/apply-01 `
  --target .private/finance.sqlite --currency EUR
```

Se a base de destino existir, é criada uma cópia antes das migrações de esquema. A carga de arquivo e movimentos usa uma transação. Falhas na carga anulam as escritas dessa transação. As alterações de esquema são uma operação de manutenção anterior e dispõem do respetivo backup.

## Arrancar e parar

Executar `Start-FinanceForgeLab.cmd`. O lançador compila, verifica a resposta do servidor e abre `http://127.0.0.1:5148`. O servidor mantém-se em segundo plano; o mesmo lançador reutiliza uma instância já pronta. Para parar, usar `Stop-FinanceForgeLab.cmd`.

Definir a palavra-passe do proprietário no primeiro acesso. Depois, adicionar as contas com datas e saldos de abertura conhecidos. O histórico sem conta identificada fica por associar. Lançamentos anteriores à abertura permanecem consultáveis e não são novamente somados ao saldo inicial.

Em caso de ligação recusada, executar o lançador e consultar `.private/runtime/build.log`, `web.stdout.log` e `web.stderr.log` se o arranque falhar. O servidor recusa arrancar se a base não existir ou tiver migrações pendentes.

## Backups e recuperação

Enquanto a aplicação funciona, são criadas cópias locais diárias e semanais, com retenção de sete diárias e quatro semanais. O ecrã de dados permite cópias manuais e exportação cifrada por palavra-passe. Guardar uma cópia exportada num destino independente do computador.

Para criar ou restaurar uma cópia SQLite para um ficheiro novo:

```powershell
dotnet run --project src/FinanceForgeLab.Migration -- backup `
  --database .private/finance.sqlite --output .private/finance-backup.sqlite

dotnet run --project src/FinanceForgeLab.Migration -- restore `
  --database .private/finance-backup.sqlite --output .private/recovered.sqlite
```

Para decifrar uma exportação, parar a aplicação e executar:

```powershell
dotnet run --project src/FinanceForgeLab.Migration -- decrypt `
  --database 'C:\caminho\FinanceForgeLab.fflbackup' `
  --output .private/recovered.sqlite
```

A palavra-passe da cópia é pedida na consola, sem a apresentar. O destino tem de ser novo. Verificar a integridade e as contagens antes de adotar a cópia recuperada:

```powershell
dotnet run --project src/FinanceForgeLab.Migration -- check --database .private/recovered.sqlite
```

É possível escolher explicitamente uma base para a aplicação:

```powershell
$recoveredPath = (Resolve-Path .private/recovered.sqlite).Path
dotnet run --project src/FinanceForgeLab.Web --no-launch-profile -- `
  --urls http://127.0.0.1:5148 --Finance:DatabasePath=$recoveredPath
```

Se existirem movimentos novos na base atual, reconciliá-los antes de a substituir por uma cópia antiga. A palavra-passe do proprietário será a que existia na data da cópia. Cópias com esquema anterior exigem atualização explícita.

## Migrações de esquema

Parar o servidor, criar backup e ensaiar a atualização numa cópia. As migrações EF Core estão em `src/FinanceForgeLab.Infrastructure/Migrations`.

```powershell
dotnet tool install dotnet-ef --tool-path .tools/ef --version 10.0.12
$databasePath = (Resolve-Path .private/finance.sqlite).Path
.tools/ef/dotnet-ef database update --project src/FinanceForgeLab.Infrastructure `
  --connection "Data Source=$databasePath;Foreign Keys=True"
dotnet run --project src/FinanceForgeLab.Migration -- check --database .private/finance.sqlite
```

Para criar uma nova migração durante o desenvolvimento:

```powershell
.tools/ef/dotnet-ef migrations add NomeDaAlteracao --project src/FinanceForgeLab.Infrastructure
dotnet test FinanceForgeLab.slnx
```

Não usar `EnsureCreated` nem atualizar o esquema automaticamente no arranque do servidor.

## Acesso e ficheiros privados

O arranque fornecido liga apenas ao computador local. O acesso pela rede doméstica requer uma configuração posterior de HTTPS e autorização. Dados, credenciais, chaves e logs ficam em `.private`; essa pasta não deve ser servida como conteúdo estático nem enviada para o GitHub.

O CSV usa UTF-8, ponto e vírgula, datas ISO e montantes com ponto decimal e quatro casas. Campos textuais com possíveis fórmulas são neutralizados.

Para os PDFs bancários, seguir o [guia de importação novobanco](importacao-novobanco.md). A pasta `Extratos` também está excluída do Git. O PDF original é incluído na base e nas suas cópias de segurança.

A API autenticada está em `/api/v1`, com consultas de movimentos, contas, categorias e dashboard. Escritas e exportações de backup exigem antiforgery; integrações autorizadas podem obter o token em `/api/v1/security/token` e enviá-lo no cabeçalho `X-CSRF-TOKEN`.
