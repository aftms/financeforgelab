# Importar extratos novobanco

O ecrã **Extratos** prepara tabelas de movimentos de contas à ordem e de poupança em EUR nos PDFs textuais novobanco. Aceita ficheiros até 20 MB e 80 páginas. O processamento é local. Documentos digitalizados ou layouts que não possam ser conferidos ficam bloqueados para importação. Outros detalhes do extrato integrado, como património ou seguros, permanecem no PDF original.

## Preparar as contas

Criar no ecrã **Contas** uma conta por número bancário, incluindo poupanças quando aplicável. Escolher uma data e um saldo de abertura conhecidos. O saldo anterior do primeiro extrato pode servir de referência, desde que corresponda ao início da data escolhida.

Um PDF pode conter várias contas. Cada uma tem a sua revisão e associação. Uma associação bancária com importações ativas não pode mudar para outra conta da aplicação. Após anular as importações afetadas, é possível preparar uma nova revisão.

## Rever e confirmar

1. Escolher um PDF em **Extratos**. O original é preservado e cada conta aparece na lista de revisões.
2. Abrir **Rever** e conferir o período, o saldo inicial, os totais de débitos/créditos e o saldo final. As datas vêm das tabelas, incluindo a passagem de ano, e não do nome do ficheiro.
3. Associar a conta da aplicação e, opcionalmente, uma pessoa. A pessoa identifica o membro da família a associar aos movimentos; não é deduzida do ficheiro.
4. Aplicar categorias em conjunto e ajustar cada linha quando necessário. Um reembolso pode ser classificado como despesa com valor positivo. Transferências entre contas da família exigem a outra conta.
5. Resolver possíveis duplicados: associar um movimento existente, excluir a linha ou confirmar expressamente que representa uma operação distinta. A comparação considera a conta, a data e o montante; descrições diferentes não eliminam o aviso.
6. Usar **Guardar revisão** para continuar mais tarde. A gravação da revisão não cria movimentos financeiros.
7. Selecionar **Confirmar importação**. É criada uma cópia de segurança prévia. Os movimentos selecionados, associações e decisões são gravados numa transação; uma falha impede a carga parcial.

O saldo final mostrado confere o próprio PDF. Não altera o saldo de abertura da conta da aplicação nem constitui uma reconciliação automática com movimentos manuais, históricos anteriores ou períodos em falta. Linhas excluídas não afetam o saldo da aplicação.

Ao importar o extrato da outra conta de uma transferência já criada, usar **Associar existente**. Criar outra receita/despesa ou uma segunda transferência duplicaria o registo dessa operação.

## Repetição e anulação

O conteúdo do PDF é identificado por SHA-256. Carregar novamente o mesmo ficheiro, mesmo com outro nome, abre as revisões ativas existentes. A confirmação repetida com a mesma revisão não cria movimentos adicionais.

**Anular importação** preserva o lote e os lançamentos, excluindo os efeitos dos movimentos criados por esse lote. Associações a movimentos anteriores são conservadas. Alterações posteriores ou associações a outros lotes bloqueiam a anulação automática de todo o lote; resolver esses movimentos individualmente antes de continuar.

Depois de anular, carregar novamente o PDF prepara uma nova revisão e conserva o lote anterior como **Anulado**. A confirmação de movimentos individuais anulados ou alterados nunca é restaurada silenciosamente.

## Original e cópias

O PDF original, a leitura extraída e as decisões ficam guardados no SQLite e são incluídos nas cópias locais e cifradas. **PDF original** permite descarregar o documento através de um endereço autenticado. Os números bancários são abreviados na interface; a informação completa permanece na base privada.

Os PDFs de trabalho podem ser colocados em `Extratos`, pasta excluída do Git, tal como `.private`. Ficheiros reais não devem ser usados como fixtures de testes publicadas.

## Análise pela linha de comandos

Para extrair e conferir sem criar revisões nem movimentos na base:

```powershell
dotnet run --project src/FinanceForgeLab.Migration -- inspect-statement `
  --file 'Extratos/exemplo.pdf' --output '.private/relatorio-extrato.json'
```

O destino tem de ser novo. O relatório contém informação financeira detalhada e deve permanecer privado; a consola apresenta apenas contagens e o estado da validação.

O leitor usa [PDFiumCore](https://github.com/Dtronix/PDFiumCore), publicado sob [Apache-2.0](https://github.com/Dtronix/PDFiumCore/blob/master/LICENSE), com as dependências nativas PDFium fornecidas pelo pacote. A versão está fixada no projeto de infraestrutura. Chamadas ao motor nativo são serializadas; os originais não são alterados.
