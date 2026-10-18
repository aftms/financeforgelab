# FinanceForgeLab — procedimentos financeiros

Antes de processar extratos, faturas, emails, imagens ou alterar dados financeiros, ler `src/FinanceForgeLab.Core/FinanceOperatingManual.cs`. É a fonte única do manual publicado em `/manual` e das regras enviadas ao Assistente em `procedures.txt`.

- Ao receber extratos, cruzar com compras/movimentos existentes e identificar contas em falta; uma associação confirmada deve atualizar o movimento e a compra, preservando categorias, totais e fontes.
- Não considerar arquivar um documento equivalente a concluir a reconciliação. Rever as pendências abrangidas pela nova informação e reportar as que persistem.
- Usar serviços financeiros, verificações de duplicados e auditoria; validar em cópia antes de alterações em lote. Não afirmar que os dados foram guardados sem verificar a persistência.
- Manter o manual e os procedimentos do código coerentes quando alterar o funcionamento. Instruções explícitas do utilizador prevalecem sobre estas regras.
- Alterações ao Gateway pertencem ao agente desse projeto; não modificar nem reiniciar o Gateway a partir deste projeto.
