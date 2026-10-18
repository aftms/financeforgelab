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

## Mudar o nome ou a localização da pasta

1. Executar `Stop-FinanceForgeLab.cmd` antes de renomear ou mover a pasta. O processo em execução tem referências ao caminho antigo; o lançador de paragem valida esse caminho antes de o terminar.
2. Renomear ou mover a pasta inteira, incluindo `.private`, `Extratos`, `.git` e `.tools`.
3. Reabrir o projeto e esta sessão de trabalho a partir da nova pasta.
4. Executar `Start-FinanceForgeLab.cmd` na nova localização. O lançador deteta caminhos antigos no cache de compilação e força a resolução de dependências e a reconstrução dos recursos quando necessário.

O endereço local continua a ser `http://127.0.0.1:5148` e o remoto Git não depende do nome da pasta. Se o Git acusar propriedade duvidosa, adicionar apenas o caminho exato do novo repositório à configuração `safe.directory`.

Credenciais, chaves, PDFs e base de dados acompanham `.private`. Os caminhos absolutos registados em auditorias antigas indicam a localização no momento da captura; não são usados para localizar os dados operacionais. Uma nova execução da migração Access recebe a localização atual do pacote.

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

## Reestruturação de categorias

A ferramenta `tools/Reclassify-Categories.py` aplica um plano privado aprovado, com backup obrigatório, auditoria na SQLite e validação dos dados financeiros. Requer Python 3.10 ou superior, sem dependências externas. O plano identifica explicitamente cada lançamento resolvido e cada lançamento ainda por rever; referências antigas são preservadas.

Ensaiar primeiro numa cópia SQLite consistente. Parar a UI pelo catálogo do Gateway antes de aplicar na base operacional. A ferramenta recusa alterações desde a preparação do plano, mistura de receitas/despesas e referências bancárias que precisem de tratamento específico. Uma repetição do mesmo lote é idempotente.

```powershell
python tools/Reclassify-Categories.py apply --database .private/finance.sqlite --plan .private/category-restructure/approved-plan-v1.json --backup .private/category-restructure/before-apply.sqlite --report .private/category-restructure/apply-report.json
```

Os ficheiros de backup e relatório têm de ser novos. As categorias substituídas ficam inativas. Em **Categorias**, a estrutura ativa aparece por defeito; **Mostrar categorias arquivadas** permite consultar a anterior. **Classificações antigas a rever** liga aos movimentos que ainda usam categorias inativas. Ao reclassificar esses movimentos, deixam de aparecer nesse grupo.

O lote auditado guarda os IDs anteriores e novos de cada lançamento, as versões dos movimentos, o estado anterior das categorias e os casos pendentes. A reversão é recusada se houver alterações posteriores nas tabelas protegidas; não substitui dados mais recentes por uma cópia antiga. As versões continuam a aumentar para invalidar formulários desatualizados.

```powershell
python tools/Reclassify-Categories.py rollback --database .private/finance.sqlite --batch-id categories-20261015-v1 --backup .private/category-restructure/before-rollback.sqlite --report .private/category-restructure/rollback-report.json
python -m unittest discover -s tests -p test_category_reclassification.py -v
```

O arquivo Access e os mapas de importação originais mantêm-se. Uma repetição da importação Access pode sinalizar `ChangedDestinationConflict` depois da reclassificação: essa proteção impede repor a organização antiga. Novos dados legados devem ser reconciliados explicitamente com a estrutura atual, nunca resolvendo o conflito por substituição dos mapas ou dos movimentos.

O Gateway executa a DLL compilada. Depois de alterar o código, parar a UI pelo Gateway, compilar `dotnet build FinanceForgeLab.slnx`, e voltar a iniciá-la pelo catálogo. A configuração atual utiliza `src/FinanceForgeLab.Web/bin/Debug/net10.0/FinanceForgeLab.Web.dll`.


### Detalhe dos movimentos históricos

A visão geral apresenta todas as categorias de despesa, incluindo as de menor valor, e liga receitas/despesas ao ano selecionado (`/movimentos?ano=2026&tipo=Income`). «Pendências de revisão» distingue linhas de extratos, compras/documentos por conferir e despesas confirmadas sem conta de pagamento, respeitando o ano selecionado e ligando a consultas filtradas. Documentos pendentes sem data são indicados separadamente e incluídos na consulta de compras para poderem ser resolvidos. As contagens podem sobrepor-se e não são somadas. Despesas sem conta são contadas por pagamento, mesmo quando têm vários registos por categoria; já entram nos totais. Documentos com conflitos ou ligações a movimentos anulados voltam à revisão; benefícios, documentos com valor financeiro zero e totais de encomendas repartidas não são tratados como despesas em falta. As linhas já registadas por confirmação parcial ou associadas a movimentos confirmados não são contabilizadas como novas operações pendentes. Valores de extratos ainda sem classificação não entram artificialmente nos totais financeiros.

Em **Movimentos**, a consulta começa no mês atual de Lisboa. **Período** permite selecionar **Mês / ano** (incluindo **Ano completo**), **Intervalo de datas** com limites inclusivos, ou **Todo o histórico**. Num intervalo pode ser indicada só a data inicial ou só a final. Alterar qualquer filtro atualiza automaticamente a lista e regressa à primeira página; a pesquisa por texto aguarda 350 ms após a última tecla. **Mês atual** regressa ao mês em curso preservando os outros filtros, e **Limpar** repõe todos os filtros e o mês atual. A paginação, as datas junto ao total e o CSV usam sempre os filtros efetivamente aplicados. As ligações de revisão de categorias arquivadas e de histórico por associar abrem explicitamente todo o histórico.

A pesquisa e o período partilham uma linha compacta; os restantes filtros ficam na linha abaixo. Clicar num dos seis cabeçalhos da tabela alterna a ordenação crescente/decrescente, indicada pela seta. A ordem abrange todos os resultados, regressa à primeira página e mantém-se na paginação e no CSV. Na coluna de descrição, os movimentos antigos são ordenados pelo detalhe visível quando não têm descrição livre.

A página inicial e a lista de movimentos mostram a categoria/subcategoria atual e a classificação original da aplicação antiga. O detalhe original é lido do arquivo `SourceRecords`, através da ligação de migração e do respetivo snapshot; não depende dos nomes atuais das categorias.

Quando a descrição livre está vazia, o título usa a subcategoria original, identificada como detalhe original. A descrição guardada não é preenchida artificialmente. Observações adicionais são apresentadas; quando coincidem com a descrição, não se repetem. A subcategoria também permanece visível na página inicial em ecrãs pequenos.

## Temas da aplicação

O seletor no cabeçalho e no ecrã de entrada permite escolher Claro (aspeto original e opção inicial), Escuro ou Sistema. A preferência fica no navegador, em `localStorage` (`financeforge.theme`), mantém-se na navegação e sincroniza entre separadores. Sistema acompanha `prefers-color-scheme`; sem acesso ao armazenamento, a escolha continua funcional durante a página atual. `theme.js` aplica a preferência antes dos estilos para evitar um clarão claro ao abrir em modo escuro. As cores originais permanecem como alternativas CSS; `themes.css` define as cores semânticas escuras para formulários, tabelas, revisão, assistente, gráficos e entrada. Não altera contas nem dados financeiros.

## Assistente: pedidos, anexos e delegação

O acesso recupera automaticamente de formulários com token de proteção inválido ou desatualizado: o POST é rejeitado sem executar login/configuração/logout e o navegador recebe uma página nova em `/Access?refresh=true`, com indicação para voltar a introduzir a palavra-passe. A validação antiforgery mantém-se ativa; o pedido não é repetido automaticamente. Correção validada em 17/10/2026 com seis testes de acesso, navegador com cookie removido entre abertura e envio e verificação HTTP na aplicação publicada. Não exige alterar credenciais ou dados financeiros.

A página `/assistente` aceita texto, PDF, PNG/JPG e TXT/CSV/TSV. Permite selecionar, arrastar ou colar ficheiros, até 10 por envio, 20 MB por ficheiro e 50 MB no total. Os originais ficam em `AssistantAttachments`, incluídos no backup SQLite. `AssistantRequests`, `AssistantTurns` e `AssistantOperations` conservam estados, respostas, identificação da delegação e recibos das operações. A migração `20261017084623_AssistantRequests` acrescenta apenas estas quatro tabelas.

O servidor consulta o Gateway; o navegador não guarda credenciais e não precisa de permanecer aberto. O contrato usa `POST /api/delegate` com `delegate.request`, `wait_timeout_seconds=0`, destino explícito `financeforgelab-assistant` e chave estável por pedido/ronda. A recuperação consulta `GET /api/delegate/{id}`. Uma ligação perdida repete a chave e o prompt originais; não cria uma nova execução. As consultas abrandam até 60 segundos e respeitam `Retry-After` até uma hora. Só uma execução desta aplicação é acompanhada de cada vez. Uma falha de callback nunca equivale a sucesso financeiro.

O bundle privado de análise é gerado em `.private/assistant/exchange/<pedido>/<ronda>/`: originais, catálogo, movimentos, compras, fontes e linhas bancárias em JSON/JSONL. Não inclui a tabela de autenticação nem credenciais. Os originais autoritativos permanecem na base; o bundle é derivado. Não publicar estas pastas. A resposta do agente tem esquema versão 1, resumo, perguntas e ações. Não se executam SQL, scripts ou comandos arbitrários devolvidos pelo agente. `AssistantActions` aplica compras, associações, importações bancárias, movimentos novos, classificações, repartições, adiantamentos e referências através dos serviços financeiros existentes. Resultados verificados são mostrados separadamente do texto da análise, com links para os movimentos (`/movimentos?registo=<id>`).

As perguntas criam uma ronda seguinte no mesmo pedido. Até oito rondas podem prosseguir automaticamente quando o agente precisa de consultar os resultados de uma etapa. Rejeições financeiras ficam visíveis como pendências; ações já concluídas têm recibos e não são repetidas. Arquivar apenas oculta o pedido da lista principal. Ficheiros de formato desconhecido e extratos em imagem não são tratados como PDFs textuais validados; o agente deve pedir informação legível ou esclarecimento, sem contornar a conferência de saldos.

### Atualizações incrementais das fontes

O Assistente inclui «Atualizar documentos das pastas» (partilha Financas) e «Verificar novos emails» (Hotmail e subpastas autorizadas; Gmail apenas INBOX, limitado aos 1000 emails disponibilizados). Cada botão cria um pedido durável; se já existir uma atualização desse tipo por concluir, abre o mesmo pedido. O estado e as perguntas continuam disponíveis ao sair da página ou reiniciar a aplicação.

A migração `20261017135633_AssistantSyncMemory` guarda versões das fontes em `AssistantSources`, com conta/pasta ou caminho, identidade, SHA-256, estado, referências do resultado e pontos de retoma por âmbito. O bundle transmite essa memória e o histórico de compras/fontes/extratos já existente. A primeira passagem ainda pode inventariar metadados históricos; nas seguintes, o agente usa os pontos guardados e uma sobreposição temporal de sete dias nos emails, sem reler corpos/documentos inalterados já tratados. O Gateway identifica a conta Hotmail como `hotmail`; o âmbito interno é `Hotmail|caminho/completo`.

`source_fetch` pede à aplicação a leitura autenticada do original pelo Gateway; os bytes são validados e guardados como anexo. `source_record` regista o resultado por fonte: Processed exige operações concluídas ou ligações verificadas a comprovativos/importações; Ignored conserva o motivo; Pending mantém dúvidas/erros. `source_cursor` avança apenas janelas declaradas completas e sem fontes pendentes nesse âmbito; erros preservam o ponto anterior. As fontes não substituem a conciliação financeira: várias fontes da mesma compra continuam a corresponder a uma única despesa. Um extrato guardado pode conservar linhas por classificar na revisão de extratos. PDFs/imagens/texto são suportados; ZIPs protegidos ficam por resolver. Até 20 leituras por ronda; ao atingir o limite de etapas, a continuação fica no mesmo pedido.

Validação em 17/10/2026: 242 testes passaram, incluindo recuperação, repetição sem duplicar, memória por versão e cursores bloqueados por pendências; leitura real de PDF e email pelo Gateway validada. A migração foi ensaiada numa cópia, preservando as 28 tabelas existentes e a integridade referencial. Não foram criadas despesas nem pedidos de atualização reais pelos ensaios.

### Ativação e responsabilidade do Gateway

O processamento está desligado por defeito. A configuração privada `.private/web/assistant-settings.json` aceita `{"Finance":{"AssistantEnabled":true}}` e é recarregada sem reinício. Só ativar depois de qualificar o destino de análise e passar o ensaio completo com dados fictícios. Enquanto desligado, a interface informa que os pedidos ficam em fila. A autenticação da aplicação ao Gateway usa as variáveis já existentes em `.env`; `FFL_ASSISTANT_TARGET` pode selecionar outro destino autorizado.

Estado em 17/10/2026: por orientação do utilizador, foi retirado o requisito adicional de isolamento pedido inicialmente pelo FinanceForgeLab. O Gateway confirmou o destino `financeforgelab-assistant` ativo no executor habitual (`codex_cli`, `development`, `workspace-write`, sessão nova, sem caller overrides nem auto-permit), sem exigir WSL/Ubuntu, novo launcher ou perfil especial. Não remover proteções existentes nem modificar o código/configuração do Gateway a partir daqui. A aplicação valida e aplica as ações estruturadas com prevenção de duplicados; isto não constitui uma garantia de isolamento do executor. A API POST/poll e os caminhos do bundle mantêm-se. Thread de coordenação: `comm-thread-ffb19eb0-86b6-490f-956d-13b50e20a448`.

Assistente ativado após ensaio completo real Gateway/Codex em base exclusivamente sintética (`.private/assistant-smoke/ready.sqlite`): leitura de anexo, pergunta sem gravação, resposta no mesmo pedido, criação de uma despesa de 12,34 EUR pela aplicação, recuperação de recibo interrompido e reenvio da mesma compra sem duplicar. O worker foi reconstruído entre consultas para verificar recuperação do ID persistido. Resultado final: uma transação e uma compra, em três delegações concluídas com callback válido. Evidência privada: `.private/assistant-smoke/verified-result.json`. A configuração local passou a `AssistantEnabled=true`; o servidor recarrega-a sem reinício. Os testes não inseriram movimentos na base real.

Antes de ativar: receber confirmação do Gateway, adaptar os caminhos do bundle se o executor os remapear, executar um pedido sintético com anexo e resposta/pergunta, conferir que só a aplicação grava uma despesa, repetir sem duplicar e testar a recuperação. As ferramentas privadas de ensaio e o backup anterior à migração encontram-se em `.private/assistant-smoke`. Não usar a base real para a despesa fictícia. As definições de testes (`Testing`) não iniciam o worker automaticamente.

### Registos por categoria e documentos de origem

Na consulta, no CSV e na contagem do painel, as repartições validadas de uma compra são apresentadas como registos agregados por categoria/subcategoria. Cada registo preserva a data e a conta do pagamento e tem uma chave composta por pagamento e categoria. O pagamento mantém uma única transação contabilística para conciliação: nunca somar o seu total às parcelas. Filtrar por categoria inclui apenas o valor dessa parcela. As repartições persistidas continuam a ser editadas em conjunto e têm de conferir com o total; não é necessária migração nem recriação de despesas existentes.

Faturas, emails, imagens e extratos são fontes, não despesas adicionais. «Origem e documentos» em cada registo mostra as fontes efetivamente associadas ao pagamento através de PurchaseEvidenceSources e BankImportEntries. O comprovativo abre no contexto da compra, o extrato permite descarregar o original. Fontes adicionadas posteriormente aparecem nos mesmos registos. O conteúdo integral fica fora da listagem normal. A introdução manual e o histórico indicam a sua origem, sem inventar documentos em falta.

Cartão Continente (17/10/2026): por autorização do utilizador, os carregamentos documentados passam a transferências para «Cartão Continente · saldo parcial», e as novas compras pagas com saldo são despesas dessa conta. `TopUp` impede aprovação como despesa ou regra de despesa; `LinkTopUpAsync` valida uma transferência equilibrada e a conta destinatária. `StoredValue` identifica o consumo de saldo. Quando «Desconto Cartao Utilizado» representa saldo gasto, o consumo inclui essa parcela mais «Cartao Cliente»; o total impresso e ambas as parcelas são conservados no detalhe e no PDF. Bónus acumulados não são novamente subtraídos dos artigos. O saldo é apenas o dos movimentos documentados desde 19/09/2026, sem saldo inicial presumido nem reconciliação integral de bónus e histórico. A EntregaZero anteriormente guardada como benefícios permanece inalterada. Os PDFs anonimizados são arquivados sem montante nem movimento.
