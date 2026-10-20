# Acesso externo por HTTPS

Esta opção segue o modelo do Admin do Gateway: Cloudflare Tunnel liga um endereço HTTPS permanente à aplicação local. A base, documentos, chaves e Assistente continuam no computador original. Não há uma segunda base nem cópias para reconciliar. O acesso local em `http://127.0.0.1:5148` continua disponível.

O computador precisa de estar ligado, com internet e sessão Windows iniciada. O túnel recupera ligações interrompidas e a tarefa volta a arrancar ao iniciar sessão. Isto não é alojamento no Render e não funciona com o computador desligado. Para esse requisito é necessária uma migração de alojamento, incluindo armazenamento persistente e acesso do Assistente às fontes locais; não copiar a base para um serviço com disco temporário.

## Preparação única

1. Compilar/testar a aplicação e criar o proprietário **localmente**. A app recusa a criação do primeiro proprietário pelo endereço público.
2. Instalar/autenticar o cliente `cloudflared` na conta Cloudflare responsável pelo domínio. Não reutilizar nem alterar o túnel, configuração, credenciais ou tarefas do Gateway.
3. Criar um túnel próprio para FinanceForgeLab e guardar as credenciais em `.private/remote-access/tunnel.json`:

   ```powershell
   New-Item -ItemType Directory -Path .private/remote-access -Force
   cloudflared tunnel create --credentials-file D:/Workspace/financeforgelab/.private/remote-access/tunnel.json financeforgelab
   ```

4. Guardar `.private/remote-access/tunnel.yml`, substituindo ID/caminhos/hostname pelos valores reais. Usar o caminho completo das credenciais e manter o Host público original; não configurar `httpHostHeader: localhost`.

   ```yaml
   tunnel: ID-DO-TUNEL
   credentials-file: D:/Workspace/financeforgelab/.private/remote-access/tunnel.json
   ingress:
     - hostname: financas.example.pt
       service: http://127.0.0.1:5148
     - service: http_status:404
   ```

5. Guardar `.private/remote-access/settings.json`:

   ```json
   { "PublicOrigin": "https://financas.example.pt", "TunnelId": "ID-DO-TUNEL" }
   ```

6. Guardar `.private/web/remote-access.json` e reiniciar **apenas a aplicação FinanceForgeLab** para carregar a configuração:

   ```json
   { "Finance": { "PublicOrigin": "https://financas.example.pt" } }
   ```

7. Validar a configuração e publicar o DNS próprio, sem substituir registos existentes:

   ```powershell
   cloudflared tunnel --config .private/remote-access/tunnel.yml ingress validate
   cloudflared tunnel route dns ID-DO-TUNEL financas.example.pt
   ```

8. Executar `Start-RemoteAccess.cmd`. Instala somente a tarefa **FinanceForgeLab Remote Access**, limitada ao utilizador atual, e abre o endereço público. Verificar entrada, gráficos, operações de consulta e ligação interativa antes de dar a publicação por concluída. Testar autenticação/escritas primeiro com dados sintéticos.

## Utilização e manutenção

- Entrar pelo endereço HTTPS com a mesma palavra-passe do proprietário. Os cookies do acesso local e remoto são separados; a base é a mesma.
- `Start-RemoteAccess.cmd` volta a iniciar o túnel sem criar outra instância da app quando ela já responde. `Stop-RemoteAccess.cmd` para somente o acesso externo; o arranque automático ao iniciar sessão mantém-se instalado.
- Para desativar permanentemente o arranque externo, parar o túnel e desativar a tarefa **FinanceForgeLab Remote Access**. Para remover também a autorização do hostname, retirar `Finance:PublicOrigin` da configuração privada e reiniciar a app. A remoção de DNS/túnel deve usar exclusivamente os recursos deste projeto.
- Registos do túnel em `.private/remote-access/*.log`. Não ativar logs de debug: podem conter cabeçalhos e dados privados. Credenciais, configurações locais, base e documentos não pertencem ao Git.
- Atualizações da app continuam a reiniciar apenas FinanceForgeLab; o túnel pode permanecer ativo durante esse reinício. O supervisor não tenta reconstruir/reiniciar a app durante a manutenção. Se a app parar, usar o lançador local para a voltar a iniciar.
- O host público só aceita tráfego recebido em loopback e identificado como HTTPS pelo túnel. Cookies HttpOnly/Secure, sessões de oito horas, antiforgery, verificação de Origin e autenticação mantêm-se ativos. As APIs/documentos exigem autenticação. Hostnames desconhecidos, forwarding para o hostname local e acessos diretos de rede são recusados.

Referências: [Cloudflare — túneis locais](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/do-more-with-tunnels/local-management/create-local-tunnel/) e [Render — armazenamento persistente](https://render.com/docs/disks).
