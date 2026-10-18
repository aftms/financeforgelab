namespace FinanceForgeLab.Core;

public static class AssistantEmailPolicy
{
    public const string ViaVerdeFolder = "06_Servicos/Transportes/Via Verde";
    public const string ViaVerdeInstruction = "Verificar sempre a pasta Hotmail 06_Servicos/Transportes/Via Verde nas pesquisas de novos emails, incluindo os extratos, faturas, recibos e respetivos PDFs de resumo e detalhe. Usar memória por mensagem/anexo (identificador, referência e hash) e ponto de retoma desta pasta; processar novos/alterados e revisitar pendências, sem reler os documentos inalterados. Cruzar os serviços e cobranças com os movimentos bancários existentes, preservar a repartição entre portagens, estacionamento e outros serviços e associar as fontes sem duplicar despesas. A data do serviço não é necessariamente a data do débito. Se o PDF não indicar a cobrança, não inventar a data ou o cartão; confirmar a correspondência por outras provas e manter pendentes as ambiguidades. A reconciliação conjunta de um ciclo exige cobertura completa do contrato e da sequência bancária, totais iguais e repartição por categoria invariável em todas as combinações compatíveis com as datas. Guardar a prova do ciclo e usar a data efetiva do banco, sem apresentar uma atribuição inferida de serviços individuais como se constasse do PDF.";
    public static object[] RequiredFolders => [new { account = "hotmail", folder = ViaVerdeFolder, cacheScope = "Hotmail|" + ViaVerdeFolder }];
}
