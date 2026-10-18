# FinanceForgeLab

Aplicação web local para gestão financeira pessoal e familiar, desenvolvida em C# com ASP.NET Core, Blazor e SQLite.

## Funcionalidades

- Contas financeiras ativas e inativas, com saldo de abertura documentado.
- Receitas, despesas e transferências entre contas, com edição e anulação rastreável.
- Categorias e subcategorias configuráveis.
- Histórico paginado, pesquisa e filtros por datas, contas, classificações e pessoas.
- Dashboard com saldos, receitas, despesas, evolução mensal e distribuição por categoria.
- Exportação CSV e cópias de segurança locais ou cifradas por palavra-passe.
- Importação de extratos PDF novobanco com revisão por conta, conferência de saldos, classificação, deteção de possíveis duplicados e anulação controlada.
- Ferramenta de migração de Microsoft Access com simulação, arquivo integral, validação e repetição segura.
- Assistente de IA através do Gateway, com pedidos acompanhados na app, atualização de documentos e emails e memória das fontes já tratadas.
- Reconciliação entre extratos, faturas e emails, com origem documental acessível e repartição das compras por categoria/subcategoria sem duplicar despesas.
- Módulo de pendências, revisão de inconsistências e manual dos procedimentos financeiros disponível em `/manual`.
- Despesas Recorrentes com totais mensais, evolução e consulta dos movimentos associados.
- Temas claro e escuro, filtros automáticos e acesso aos movimentos a partir dos gráficos.

## Requisitos

- SDK .NET 10, conforme `global.json`.
- Para extrair uma base Access: Windows PowerShell 5.1 e Microsoft ACE OLE DB 16.0 compatível com a arquitetura do processo.
- Uma base SQLite preparada pela ferramenta de migração antes de iniciar o servidor.

O funcionamento normal da aplicação não depende de Access ou Office. O lançador fornecido destina-se a Windows. O diretório `.private` guarda os dados locais e está excluído do Git.

## Compilar e testar

```powershell
dotnet build FinanceForgeLab.slnx
dotnet test FinanceForgeLab.slnx
dotnet run --project src/FinanceForgeLab.Migration -- --help
```

## Preparar os dados e iniciar

Um clone deste repositório não inclui bases de dados nem credenciais. Segue o [procedimento local de migração e operação](docs/operacao-local.md) para preparar a tua base, simular e validar a importação.

Depois de a base estar disponível em `.private/finance.sqlite`, executa **`Start-FinanceForgeLab.cmd`**. O lançador verifica o servidor antes de abrir **http://127.0.0.1:5148**. O primeiro acesso pede que definas a palavra-passe do proprietário.

O servidor continua em segundo plano; executar novamente o lançador reutiliza a instância existente. Para parar, executa **`Stop-FinanceForgeLab.cmd`**. Os registos ficam em `.private/runtime`.

## Estrutura

| Projeto | Responsabilidade |
| --- | --- |
| `FinanceForgeLab.Core` | Entidades e precisão monetária |
| `FinanceForgeLab.Infrastructure` | EF Core, SQLite, migração, regras financeiras e backups |
| `FinanceForgeLab.Migration` | Simulação, importação, validação e recuperação |
| `FinanceForgeLab.Web` | Interface Blazor, autenticação local e API |
| `FinanceForgeLab.Tests` | Regras financeiras, preservação, integridade, autenticação e testes HTTP |

## Preservação e segurança

Valores monetários são guardados como inteiros de 64 bits à escala de quatro casas decimais. Transferências são atómicas e não entram nos totais de receitas/despesas. Histórico sem conta identificada permanece por associar; movimentos anteriores à abertura não são novamente somados ao saldo inicial.

A aplicação exige autenticação, protege os pedidos de escrita e liga apenas ao computador local. As chaves de proteção de cookies são protegidas por DPAPI no Windows. As exportações de backup podem ser cifradas com uma palavra-passe própria.

As alterações de esquema são explícitas, precedidas de backup. O servidor recusa arrancar com migrações pendentes e não atualiza automaticamente a base no arranque.

Este repositório contém código e documentação geral. Dados reais, relatórios privados de auditoria, credenciais, documentos e cópias de segurança não devem ser publicados.

A pasta local `Extratos` está excluída do Git. O [guia de importação novobanco](docs/importacao-novobanco.md) explica a preparação, a revisão e a confirmação dos PDFs.

## Estado

Estão implementados a gestão de movimentos individuais, a migração histórica, a reconciliação documental, o Assistente, as pendências e as Despesas Recorrentes, com testes financeiros, de integridade e HTTP. O manual da aplicação é também a fonte dos procedimentos fornecidos ao Assistente.

Os leitores bancários suportam os layouts textuais identificados de novobanco e Revolut, incluindo os formatos de consulta bancária previstos no código, e conferem os saldos antes da confirmação. Outros layouts e importação genérica de CSV/Excel/OFX exigem leitores próprios. Orçamentos permanecem para desenvolvimento posterior. O Assistente depende de uma configuração local autorizada do Gateway; as credenciais não fazem parte deste repositório.
