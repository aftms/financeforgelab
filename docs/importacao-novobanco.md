# Importar extratos bancários

## Faturas com várias categorias

As compras podem conservar uma repartição por categorias cuja soma tem de coincidir com o total da fatura. O pagamento mantém um único movimento e lançamento bancário. Os relatórios repartem a despesa pelas categorias dos artigos; a pesquisa por categoria encontra esse pagamento e o detalhe mostra a repartição. Em **Compras e pagamentos**, a repartição pode ser ajustada sem alterar o total financeiro. Uma alteração posterior ao valor do movimento invalida o uso da repartição até nova conferência.

O total da fatura e o montante bancário são campos distintos. Pagamentos mistos repartem a parte bancária proporcionalmente pelas categorias, conservando os cêntimos. Consumos pagos apenas com benefícios do cartão podem ser guardados sem criar outra despesa bancária. Descontos já refletidos no valor dos artigos não voltam a ser subtraídos; créditos acumulados para uso futuro também não reduzem o pagamento atual.

As faturas PDF anexadas às fontes são guardadas na base, verificadas pelo SHA-256 e incluídas nas cópias de segurança. Repetir o mesmo PDF/origem ou a mesma encomenda conserva a compra existente. O filtro por comerciante e a paginação permitem consultar as compras sem carregar todo o histórico. O filtro Continente mostra os pagamentos sem fatura desde a mais antiga disponível; possíveis carregamentos são apenas assinalados para identificação, sem serem assumidos como compras adicionais.

A migração `PurchaseReceiptClassification` acrescenta os campos de repartição, pagamento e arquivo do PDF. Aplicar com cópia de segurança e ensaio prévio.

O ecrã **Extratos** prepara tabelas de movimentos de contas à ordem e de poupança em EUR nos PDFs textuais novobanco e Revolut. Aceita ficheiros até 20 MB e 80 páginas. O processamento é local. Layouts que não possam ser conferidos ficam bloqueados para importação. Outros detalhes do extrato integrado, como património ou seguros, permanecem no PDF original.

O leitor novobanco também reconhece a poupança programada e linhas de valor zero, excluídas da criação de movimentos por defeito. Compras com cartão podem anteceder o início do período até sete dias; todos os saldos e totais continuam sujeitos à conferência integral. Descrições longas e tabelas indentadas nas páginas seguintes conservam a classificação das colunas.

O leitor Revolut suporta os layouts em inglês e português e os períodos separados pela passagem para a sucursal portuguesa. Dois IBAN explicitamente apresentados juntos no cabeçalho conservam a relação entre os identificadores; os extratos seguintes reutilizam a conta existente. Não se deduz esta relação por montante, data, nome ou últimos dígitos. Secções noutras moedas permanecem no PDF e mostram um aviso na revisão; não são convertidas nem lançadas como EUR. A soma dos movimentos, os totais de débitos/créditos e o saldo final têm de conferir.

A ingestão administrativa de digitalizações pode usar `StageOcrAsync`: recebe o SHA-256 esperado e as linhas extraídas, conserva o PDF original intacto e arquiva a extração OCR ao lado do PDF. Usa as mesmas conferências financeiras do leitor novobanco. Erros numa secção impedem a respetiva confirmação. O carregamento normal pela interface continua a exigir texto no PDF.

A lista de documentos permite filtrar por conta e ano, com 20 PDFs por página. Todos os documentos permanecem acessíveis, incluindo quando existem mais de 100.

## Preparar as contas

Ao carregar um PDF válido, a aplicação reconhece cada conta pelo número bancário completo. Se esse número ainda não estiver associado, cria automaticamente uma conta bancária ou poupança em EUR, com a data inicial e o saldo anterior do extrato como abertura. O nome inclui o produto e os quatro últimos dígitos; pode ser alterado em **Contas**. A criação e a origem da abertura ficam auditadas.

Novos extratos do mesmo número reutilizam a conta, incluindo associações de revisões ainda por confirmar ou de lotes anulados. Não duplicam a abertura nem alteram a data ou o saldo inicial, mesmo que tragam um período anterior. Contas diferentes com os mesmos quatro últimos dígitos continuam separadas. Secções com erros de leitura ou conferência não criam contas automaticamente.

As despesas e receitas do PDF só são criadas após a revisão e confirmação. Dinheiro vivo pode ser registado numa conta do tipo **Dinheiro**, criada em **Contas**.

Cada PDF aparece uma única vez na lista, com o número total de movimentos e quantos estão por rever. **Rever movimentos** abre diretamente uma parte com movimentos. Partes sem movimentos não exigem classificação ou confirmação; problemas de leitura continuam acessíveis. Um PDF pode conter movimentos de várias contas; **Guardar e ver mais movimentos** preserva as escolhas e permite continuar a revisão. A conta de origem e os saldos ficam em **Origem dos movimentos e conferência de saldos**. A associação do número bancário mantém-se também após anular uma importação. Carregar novamente o PDF permite preparar uma nova revisão na mesma conta.

## Rever e confirmar

1. Escolher um PDF em **Extratos**. O original é preservado e o PDF aparece uma única vez na lista.
2. Abrir **Rever movimentos** para ler as descrições e os valores. A secção **Origem dos movimentos e conferência de saldos** permite conferir a conta, o período, o saldo inicial, os totais de débitos/créditos e o saldo final. As datas vêm das tabelas, incluindo a passagem de ano, e não do nome do ficheiro.
3. Conferir a conta associada automaticamente e escolher, opcionalmente, uma pessoa. Uma associação anterior inequívoca de pessoa é sugerida; para contas novas, o titular não é deduzido do nome do ficheiro.
4. Rever as categorias/subcategorias já sugeridas a partir do histórico confirmado e de entidades ou operações reconhecidas na **Descrição no extrato**. Cada sugestão indica a origem e a confiança. Aprovar as sugestões ou alterar diretamente os campos. O filtro **Mostrar só por resolver** destaca linhas sem classificação e decisões pendentes. A opção **Classificar várias linhas de uma vez** permite aplicar categorias em conjunto. Um reembolso pode ser classificado como despesa com valor positivo. Transferências entre contas da família exigem a outra conta, que pode ser sugerida a partir do histórico da mesma conta e direção.
5. Rever os possíveis duplicados, incluindo movimentos manuais e importações anteriores. A comparação usa o montante com o mesmo sinal e a conta: a mesma data gera aviso mesmo que a descrição seja diferente. Também considera a data-valor e diferenças até três dias quando a entidade/descrição corresponde, incluindo datas fora do período do PDF. Movimentos antigos do mesmo agregado sem conta associada aparecem como candidatos e exigem revisão explícita. Cada candidato mostra data, valor, descrição, origem e motivo da correspondência. Uma correspondência única com a mesma conta, data, montante e descrição fica com **Associar existente** sugerido para aprovação. Correspondências ambíguas ficam **Por rever**. Pode-se associar o existente, excluir a linha ou confirmar expressamente que representa uma operação distinta.
6. Usar **Guardar revisão** para continuar mais tarde. A gravação da revisão não cria movimentos financeiros.
7. Selecionar **Aprovar e importar**, depois de resolver as linhas pendentes. Esta ação aprova as classificações apresentadas. É criada uma cópia de segurança prévia. Os movimentos selecionados, associações e decisões são gravados numa transação; uma falha impede a carga parcial.

As sugestões são calculadas localmente, sem enviar descrições ou valores a serviços externos. O histórico considera apenas movimentos confirmados do mesmo agregado, com categorias ativas e raiz ativa, comparando descrições sem acentos, referências numéricas e termos bancários comuns. Uma classificação histórica inequívoca prevalece sobre as regras de entidades; históricos contraditórios ficam por classificar. As regras selecionam apenas categorias existentes e não escolhem arbitrariamente entre nomes repetidos. A confiança é uma indicação da origem e repetição do padrão, não uma probabilidade estatística.

As escolhas guardadas são preservadas ao reabrir. **Sugerir linhas sem categoria** preenche apenas campos vazios de linhas a importar ou por rever, mantendo o tipo escolhido e as classificações, exclusões e associações existentes. Confirmar uma classificação ou corrigi-la nos movimentos atualiza o histórico usado nas próximas sugestões. Linhas desconhecidas ou ambíguas e possíveis duplicados continuam a exigir uma decisão explícita; nenhuma sugestão cria movimentos antes da aprovação.

O saldo final mostrado confere o próprio PDF. Não altera o saldo de abertura da conta da aplicação nem constitui uma reconciliação automática com movimentos manuais, históricos anteriores ou períodos em falta. Linhas excluídas não afetam o saldo da aplicação.

Ao importar o extrato da outra conta de uma transferência já criada, usar **Associar existente**. Criar outra receita/despesa ou uma segunda transferência duplicaria o registo dessa operação.

Associar preserva os valores, a classificação e a conta do movimento original; um histórico sem conta continua sem conta. Não cria outro movimento nem volta a somar o montante. A confirmação volta a verificar os candidatos e a versão do movimento, incluindo registos introduzidos depois de abrir a revisão. Um movimento não pode ser associado a duas linhas do mesmo lote. Linhas do próprio PDF com a mesma data e montante também geram aviso, mesmo com descrições diferentes; não são excluídas automaticamente.

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


### Referências explicadas e compras de outros serviços

Em **Rever movimentos → Referências e compras**, guarda o significado de um código bancário ou uma entidade, a categoria e a conta correspondente. A referência pode ser alterada ou desativada. Os números mantêm os zeros iniciais e são comparados como códigos completos; uma prestação de crédito pessoal não herda a finalidade de outro contrato. CUF, Luz Saúde, restauração (incluindo Tasca) e prestações de crédito pessoal têm sugestões próprias, usando as categorias ativas. Uma cobrança CUF usa a categoria principal Saúde, não o seguro com o mesmo nome.

Para compras pontuais, indica entidade, data de compra e montante efetivamente cobrado em EUR. A correspondência exige a mesma entidade, montante exato e débito entre a data indicada e sete dias depois. Uma compra que corresponda a várias linhas, ou várias explicações diferentes para a mesma linha, exige revisão. Amazon/PayPal, só pelo nome, não determinam o artigo comprado. Códigos de encomenda podem não constar do banco: conservam-se no detalhe, sem se tornarem automaticamente um requisito de correspondência bancária.

**Lista ou foto de compras** aceita PNG/JPG (até 10 MB) e TXT/CSV/TSV em UTF-8. Pode também ser colado texto. Formato recomendado: `Data;Montante;Descrição;Encomenda`, uma cobrança por linha; a última coluna é opcional. Por exemplo, `02/07/2026;29,90;Livro de cozinha;123-1234567-1234567`. A imagem é lida localmente pelo Tesseract instalado (caminho alternativo na variável `FFL_TESSERACT`); o texto extraído e as propostas são editáveis. Não são enviados ficheiros para serviços externos. As imagens temporárias são eliminadas após a leitura; ficam guardadas as referências aprovadas, não as fotografias. A leitura atual usa o modelo `eng` instalado. Fotografias pouco nítidas, datas incompletas, vários preços e encomendas com cobranças repartidas podem precisar de correção. Não se inventam montantes nem categorias quando há ambiguidade.

Aprovar uma referência guarda a explicação e atualiza sugestões vazias ou ainda correspondentes à sugestão anterior; preserva correções manuais. **Aprovar e importar** continua a ser o passo que cria os movimentos. O texto bancário original mantém-se na descrição; o significado indicado pelo proprietário passa para as observações. Referências e auditoria são guardadas em SQLite e incluídas nos backups. Não é gravada nenhuma finalidade específica para um contrato sem informação do proprietário.

A migração `BankClassificationReferences` cria apenas a tabela das referências e os índices/relações. Deve ser aplicada explicitamente após backup e verificação numa cópia, antes de reiniciar o servidor. A aplicação continua a recusar migrações pendentes no arranque.


### Escolher categoria e aceitar sugestões

A revisão, a classificação em lote e as referências usam dois campos: **Categoria** e **Subcategoria**. A lista de subcategorias depende da categoria escolhida; mudar a categoria limpa a subcategoria anterior. É possível manter apenas a categoria principal, escolhendo **Sem subcategoria**. As sugestões que indicam uma subcategoria preenchem automaticamente os dois campos.

Em cada linha, **Aceitar sugestão** confirma a classificação já preenchida e mostra **Sugestão aceite**. Se o rascunho tiver outros valores, **Aplicar sugestão** preenche o tipo, a categoria/subcategoria, a outra conta quando aplicável e o detalhe conhecido. **Aceitar sugestões disponíveis** trata em conjunto as linhas por preencher ou com sugestões ainda não aceites, preservando alterações manuais, exclusões e associações existentes. A aceitação fica guardada ao guardar a revisão. Alterar a classificação volta a marcar a linha como escolha manual.

Aceitar a classificação não autoriza duplicados nem cria movimentos; esses casos continuam por resolver, e **Aprovar e importar** mantém-se como confirmação financeira final.

### Informação extraída de compras e pagamentos

**Compras e pagamentos** (`/compras`) conserva informação interpretada de documentos antes de se conhecer a conta bancária: data, total, entidade, artigos, número de encomenda, categoria proposta, origem e dúvidas de leitura. A informação preparada só passa a movimento financeiro ao aprovar, sem exigir um extrato bancário. A interpretação assistida de imagens da conversa pode ser guardada aqui; o carregamento na revisão do extrato continua a usar o leitor local de imagem/texto descrito acima. Não existe sincronização automática de emails nesta página.

O serviço `StageEvidenceAsync` guarda até 100 registos por lote numa transação, exige uma chave de origem estável e permite repetir o lote sem duplicar os registos. Uma origem repetida com outra data ou valor é rejeitada. Para encomendas, usar a plataforma e o número de encomenda como chave; juntar previamente as informações da lista e do detalhe da mesma encomenda. As origens PayPal sem identificador de transação devem ser tratadas como provisórias: não deduplicar pagamentos distintos apenas porque têm o mesmo valor.

A lista procura possíveis correspondências nos movimentos confirmados e nos extratos em revisão, pelo canal, montante exato e data de débito entre a compra e sete dias depois. O resultado é indicativo: cobranças repartidas, agregadas ou mais tardias precisam de revisão. Com data, total, entidade e categoria confirmados, **Aprovar e criar movimento** cria já a despesa a partir da imagem ou email. A conta pode ficar **Por associar**. Antes de criar, a aplicação verifica novamente os movimentos existentes; um possível duplicado exige associação ao existente ou confirmação de compra distinta. A criação e a ligação à compra são atómicas e repetir a aprovação devolve o mesmo movimento. As dúvidas de leitura ficam visíveis. Conservam-se os dados transcritos e a descrição da origem; as imagens da conversa não são arquivadas por este serviço.

A migração `PurchaseEvidenceInbox` acrescenta apenas a tabela de informação de compras, com relações e índice único de origem por agregado. Aplicar após backup e validação numa cópia, antes de reiniciar a aplicação.

### Uma compra, várias fontes

Extratos com o mesmo nome ou período podem pertencer a pessoas e contas diferentes. A lista e o cabeçalho da revisão mostram o nome da conta, a referência bancária mascarada e a pessoa associada, quando indicada. Os PDFs são identificados pelo seu conteúdo, e a associação bancária usa a referência completa; os últimos quatro dígitos e o período não identificam uma conta. Os duplicados de contas já identificadas são comparados dentro da conta correspondente. Movimentos antigos ou compras ainda sem conta continuam a exigir conferência da origem.

Uma imagem e um email com a mesma plataforma e número de encomenda são guardados como fontes da **mesma compra**. A origem e os dados extraídos de cada documento permanecem disponíveis. Repetir uma origem já recebida não acrescenta outra fonte. Números de encomenda diferentes continuam a identificar compras diferentes, mesmo que a data e o valor coincidam. Sem número de encomenda comum, valor/data iguais não autorizam uma fusão automática.

Se os documentos da mesma encomenda indicarem datas ou totais diferentes, a compra fica sinalizada com conflito e a referência de classificação é desativada. A lista permite escolher a data e o total do documento correto, conservando todos os documentos, e aprovar os dados corrigidos. Se já existir associação a um movimento, primeiro é necessário remover essa associação para alterar os dados da compra.

**É esta compra — associar ao movimento** liga as fontes a uma despesa existente, sem alterar os lançamentos, saldos ou descrição bancária. A validação repete-se no momento da associação: movimento confirmado, mesmo agregado, entidade compatível, valor exato, conta compatível e data até sete dias depois da compra. Um movimento não pode ser reutilizado por duas compras neste fluxo; cobranças agregadas/repartidas necessitam de tratamento específico. A associação é reversível e auditada. Movimentos posteriormente alterados ou anulados ficam indicados como ligações a rever.

A migração `PurchaseEvidenceSources` preserva os registos existentes como fontes iniciais e acrescenta a identidade de encomenda e a ligação ao movimento financeiro. O serviço aceita informação extraída de imagens ou emails; não faz leitura automática de caixas de correio.

### Adiantamentos e reembolsos

**Reembolsos a receber** (`/reembolsos`) conserva o pagamento original, a entidade devedora, o valor recebido e o valor em falta. Permite consultar apenas os pendentes ou incluir os já reembolsados. Um adiantamento é registado uma só vez por movimento; as associações a recebimentos são auditadas e reversíveis, sem eliminar o recebimento financeiro.

As sugestões cruzam a entidade, a data e a conta conhecida do pagamento com movimentos confirmados e créditos dos extratos em revisão. Um crédito de outra conta não é sugerido quando a conta do adiantamento é conhecida. Um recebimento pode liquidar parcialmente um adiantamento ou repartir-se por vários, sem reutilizar o montante já associado. A confirmação de um crédito do extrato conserva a sua descrição/data/conta e cria um único movimento de reembolso, que compensa a categoria da despesa original; uma importação posterior do mesmo extrato propõe associar esse movimento. As linhas restantes do extrato mantêm a revisão existente.

Uma anulação do recebimento volta a deixar o valor por receber. Alterações ao valor do pagamento original ficam por conferir. A ausência de correspondência nos extratos disponíveis conserva o pendente e não prova ausência de pagamento em períodos ainda não carregados. A migração `ReimbursementClaims` acrescenta as tabelas de adiantamentos e associações; aplicar com backup e ensaio numa cópia.

### Primeiro documento suficiente, depois conciliação

O serviço `ConfirmRowsAsync` permite confirmar apenas as linhas identificadas com segurança, conservando o extrato em revisão e as restantes decisões. Cada linha confirmada guarda uma ligação bancária duradoura e passa a «Associar existente»; confirmar depois o extrato completo reutiliza esses registos. São repetidas as verificações do PDF, conta, pessoa, versão, categoria e possíveis duplicados. Uma repetição não cria outro movimento. Linhas ambíguas e transferências entre contas da família precisam de classificação própria antes de integrar os totais de receitas/despesas.

Na revisão, «Classificado» identifica a categoria selecionada e «Registado» identifica uma linha já incluída nos registos financeiros. Uma linha registada não recebe um aviso de possível duplicado por corresponder ao seu próprio registo. Reabrir uma revisão atualiza também a lista de categorias, incluindo subcategorias criadas durante a sessão.

Os cartões dos PDFs apresentam separadamente os movimentos registados, excluídos, por classificar, com categoria por validar e associações por confirmar. Linhas excluídas ou de valor zero não mantêm o PDF pendente. Num PDF com várias contas, «Rever movimentos» dá prioridade às secções ainda pendentes; uma secção já registada pode continuar em estado técnico `Draft` sem voltar a ser contada como por classificar. Ao confirmar uma associação parcial, a revisão passa a mostrar a categoria do movimento financeiro existente.

### Aprovação por linha, seleção ou movimentos semelhantes

Na revisão compacta, «Aprovar» confirma só a respetiva linha. As caixas de seleção e «Aprovar seleção» permitem confirmar várias linhas sem concluir as restantes; «Selecionar prontos» seleciona as linhas visíveis com classificação ou associação válida. Erros de leitura, conta em falta, classificações incompletas e possíveis duplicados sem decisão impedem a aprovação. Os registos existentes aparecem resumidos, com categoria/subcategoria por extenso, sem os campos de criação. «Ocultar registados» deixa visíveis apenas as linhas que ainda podem precisar de tratamento.

«Ver semelhantes noutros extratos» prepara uma lista dos movimentos pendentes da mesma conta, entidade/referência, tipo, categoria/subcategoria, sentido do pagamento e eventual conta de destino. A pesquisa abrange os extratos carregados da conta. Plataformas como PayPal/Amazon exigem uma referência recorrente específica; linhas já registadas, com erros, classificações diferentes, escolhas pendentes ou possíveis duplicados ficam fora do grupo. Antes de escrever, a aplicação apresenta a lista de datas, documentos e valores. «Aprovar estes N movimentos» repete a verificação da lista e das versões e confirma todos numa única transação. Qualquer alteração ou falha de integridade cancela o grupo inteiro, sem confirmações parciais.

A origem não determina quando se pode registar uma despesa: uma imagem, email ou comprovativo com informação suficiente pode dar origem ao movimento em **Compras e pagamentos**, sem esperar pelo banco. O registo tem origem `Purchase`, conserva as fontes e pode ser editado nos movimentos. Quando a conta ainda não foi identificada, a despesa entra nos totais financeiros com conta por associar; só afeta o saldo de uma conta depois de essa conta ser identificada.

Ao chegar o extrato, a deteção de duplicados também usa a compra ligada ao movimento: mesma entidade/plataforma, montante e débito até sete dias depois da compra, mesmo que a descrição do movimento já tenha sido enriquecida com os artigos. Uma correspondência única é proposta como **Associar existente**; correspondências partilhadas ou repetidas exigem revisão. Aprovar a associação não cria outra despesa. Se o movimento criado pela compra ainda não tiver conta, essa aprovação identifica a conta do pagamento, com auditoria; preserva a data original, o valor, a categoria e a descrição. As restantes associações mantêm o comportamento anterior.

Novos documentos com a mesma identidade de encomenda juntam-se à compra existente sem voltar a criar o movimento. Informação insuficiente, diferenças entre documentos ou possíveis duplicados permanecem por resolver. A leitura automática de email continua dependente do acesso e das pastas disponíveis no gateway.

### Encomendas com cobranças por envio

`SplitEvidenceAsync` conserva a confirmação original da encomenda e cria comprovativos subordinados para cobranças documentadas por envio. As parcelas têm referências e fontes distintas, categorias próprias e devem somar exatamente o total original. A operação é auditada e atómica; repetir os mesmos dados devolve as parcelas existentes. Encomendas já registadas financeiramente não podem ser repartidas por este método.

O total da encomenda passa a resumo, sem criar uma terceira despesa. Cada parcela pode criar o seu movimento a partir do email e depois ser associada à linha bancária correspondente. A lista de compras mostra os artigos, montantes e movimentos dos envios. Uma confirmação original recebida novamente preserva a repartição; escolher outra fonte não pode desfazê-la silenciosamente. A migração `PurchaseShipmentCharges` acrescenta a relação entre encomenda e cobranças; aplicar com cópia de segurança e ensaio numa cópia.

Pagamentos «Continente Portugal» em múltiplos de 10 € são sinalizados como possíveis carregamentos, sem presumir que o sejam. A descrição genérica e o histórico de supermercado não geram uma classificação automática. A revisão mostra faturas próximas pagas com saldo do cartão, conservando a distinção entre hipótese e correspondência provada. Estes pagamentos não entram na aprovação automática por grupo; criar uma despesa exige classificação verificada explicitamente ou uma referência de compra concreta, por entidade, data e montante. Associar uma compra existente continua disponível quando há correspondência documental.

### Cópias de consultas do banco

`StageOnlineCopyAsync` aceita uma cópia UTF-8 com cabeçalho `Conta NNNN NNNN NNNN (DD-MM-AAAA HH:mm:ss)` e movimentos em ordem decrescente, com data, data-valor, descrição, montante com sinal e saldo com sinal. Linhas vazias e «Ver Detalhes» são ignoradas; qualquer outra linha ilegível impede a importação. Todos os saldos consecutivos têm de conferir. A conta deve estar previamente identificada por um extrato; a cópia nunca cria uma conta ou um saldo de abertura.

A fonte fica arquivada integralmente na base de dados, com SHA-256 e auditoria, e aparece como «Cópia fornecida», com descarga em texto. A sequência confere apenas os movimentos apresentados, sem provar a cobertura integral do período. A revisão, as decisões pendentes e a deteção de duplicados usam os mesmos serviços dos PDFs. As duas pontas de uma transferência entre contas conhecidas podem associar-se ao mesmo movimento, conservando as duas fontes e excluindo a transferência das receitas e despesas.
