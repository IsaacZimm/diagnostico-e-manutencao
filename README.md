# Diagnóstico e Manutenção

Aplicativo em C# e WPF para Windows 11 x64. Extraia toda a pasta de publicação e abra **DiagnosticoManutencao.exe**. A distribuição inclui o runtime do .NET. O painel roda com as permissões da conta atual, e o reparo usa autorização administrativa para executar a operação selecionada.

O menu lateral e as categorias de hardware usam como referência o projeto público de GustavoReset. [Repositório de referência](https://github.com/GustavoReset/ResetExplode)

## Usar a interface

- **Sistema.** Consulte Windows, CPU, placa-mãe, BIOS, capacidade instalada, slots, limite informado pelo firmware e dados de cada módulo de memória. **Placa de vídeo** mostra VRAM, driver, recursos do DirectX e dados adicionais NVIDIA quando disponíveis, inclusive núcleos CUDA. **Discos** mostra espaço usado e livre, saúde, temperatura e vida útil estimada reportada pelo dispositivo. **Energia** mostra a fonte de alimentação, o plano ativo e os dados disponíveis da bateria de notebooks.
- **Sensores ao vivo.** Filtre por tipo de leitura. A tabela mostra valores atuais, mínimos e máximos, incluindo temperatura, carga, clocks, potência, controle, fatores, níveis, dados e taxas quando as fontes oferecem essas métricas. **Sistema** mantém as leituras de temperatura ACPI do inventário. **Exportar relatório** inclui todas as categorias da última coleta.
- **Jogos.** A biblioteca procura instalações locais de Steam, Epic Games, GOG e Ubisoft Connect. **FPS estimado** usa referências aproximadas de um catálogo offline para combinações conhecidas de CPU e GPU. Selecione resolução e qualidade gráfica. **Análise de gargalos** usa amostras de carga de hardware em sessões de 1, 3, 5 ou 10 minutos. **Overlay** permite escolher indicadores, cor e posição para jogos em janela ou sem bordas.
- **Benchmarks.** Escolha renderização em CPU, cena 3D Direct3D 11, ray tracing DXR, simulação de jogo, memória, carregamento de mapa, disco ou estresse curto de CPU. As amostras de armazenamento usam arquivos temporários. Compare resultados do mesmo teste e das mesmas configurações.
- **Rede e testes rápidos.** Veja adaptadores, endereços locais IPv4 e IPv6, máscaras, gateways e velocidade do link. **Testes de diagnóstico** mede a resposta de um gateway local e executa verificações de CPU e de leitura e gravação de 4 MiB em temporários. O teste **Conexão de internet** usa até 10 MiB de dados de amostra com a Cloudflare e mede DNS, latência, jitter e taxas de transferência. O Windows pode atender a leitura do disco pelo cache.
- **Otimização.** Clique em **Realizar verificação** para medir CPU, RAM, o tempo ativo do disco físico mais ocupado e o uso do motor gráfico mais ocupado. Os cartões exibem os processos com maiores medições, com nome e PID. **Realizar otimização** aparece nos cartões com ações elegíveis. Selecione e confirme as ações. O aplicativo mede novamente antes de aplicar e atualiza os cartões após os ajustes.
- **Inicialização, aplicativos e drivers.** O painel lista entradas de inicialização e abre suas configurações no Windows. O catálogo de aplicativos usa o WinGet e abre a instalação sob demanda. O inventário de drivers permite filtrar por dispositivo, fornecedor ou versão e acessar o Gerenciador de Dispositivos e as atualizações opcionais do Windows.
- **Arquivos temporários.** A prévia examina arquivos `.tmp` e `.temp` criados e modificados há mais de sete dias em `%LOCALAPPDATA%\Temp`. Após confirmação, a limpeza exclui permanentemente os arquivos elegíveis da prévia e omite arquivos em uso, modificados, recriados ou protegidos. As pastas são preservadas.
- **Reparo do Windows.** Selecione um dos cinco comandos do DISM ou a verificação `sfc /scannow` e clique em **Executar operação**. A ferramenta roda na instalação atual do Windows e exibe a saída sem abrir uma janela de terminal. A barra fica indeterminada até receber um percentual. A operação termina quando o comando sai, e o painel mostra o resultado e o caminho do registro do Windows.

A verificação de desempenho destaca em vermelho os cartões com o maior percentual positivo medido e sinaliza o alerta de uso elevado conforme o limite de cada recurso.

A VRAM dedicada vem da capacidade informada pelo adaptador gráfico no DXGI. A velocidade de rede descreve o link local que o Windows informa, em Mb/s ou Gb/s. Alguns periféricos fornecem apenas uma descrição genérica ao Windows.

A verificação usa uma amostra de aproximadamente dois segundos para CPU, disco e GPU. O painel marca CPU elevada a partir de 50% de uso total ou quando um processo consome o equivalente a pelo menos 90% de um processador lógico. Os limites de uso elevado são 85% para RAM e disco e 90% para o motor gráfico mais ocupado. Esses limiares descrevem a amostra, e a disponibilidade das leituras depende do Windows, do driver e dos processos acessíveis. A RAM residente de um processo pode incluir memória compartilhada. O cartão de disco mostra a E/S geral dos processos separadamente, pois esse contador também inclui rede e dispositivos.

As ações suportadas são reduzir a prioridade de processos elegíveis sob carga de CPU e solicitar o fechamento normal de aplicativos reconhecidos que contribuam para o uso elevado de RAM ou GPU. O cartão de armazenamento é diagnóstico. Use **Restaurar prioridades** para desfazer ajustes nos processos selecionados. Novos subprocessos podem herdar a prioridade reduzida. O aplicativo tenta restaurar os processos que ajustou ao fechar normalmente. Se for encerrado à força, a prioridade ajustada pode continuar até o processo afetado encerrar.

No reparo, apenas uma operação fica ativa por vez. `RestoreHealth`, `StartComponentCleanup` e `sfc /scannow` pedem confirmação antes da autorização administrativa. O DISM usa `/NoRestart` e informa se uma reinicialização for necessária. Os registros padrão do Windows ficam em `%WINDIR%\Logs\DISM\dism.log` e `%WINDIR%\Logs\CBS\CBS.log`.

## Recursos que dependem do computador

- **Sensores e SMART.** A disponibilidade depende do firmware, do driver e das permissões. Alguns sensores do LibreHardwareMonitor usam o driver opcional assinado [PawnIO](https://pawnio.eu/).
- **FPS ao vivo e ray tracing.** O FPS de apresentação usa eventos ETW de DXGI e Direct3D 9 quando o Windows permite a captura. O HUD continua exibindo os indicadores de hardware disponíveis. O benchmark de ray tracing requer uma GPU e um driver com suporte a DXR 1.1.
- **WinGet.** A tela de instalação de aplicativos usa o [Instalador de Aplicativo da Microsoft](https://learn.microsoft.com/pt-br/windows/package-manager/winget/).
- **Iluminação iCUE.** Para um dispositivo Corsair compatível, mantenha o iCUE aberto, habilite o iCUE SDK nas configurações e selecione a DLL de `iCUESDK/redist/x64` do pacote oficial **4.0.84**. A conexão permite escolher um dispositivo e aplicar uma cor. Desconecte para devolver o controle ao iCUE. [Instruções da Corsair](https://help.corsair.com/hc/en-us/articles/4406125301005-iCUE-How-to-Set-up-software-and-game-integrations-in-iCUE) · [SDK oficial](https://github.com/CorsairOfficial/cue-sdk/releases/tag/v4.0.84)

Os avisos de código adaptado e bibliotecas estão em **THIRD-PARTY-NOTICES.txt** e na pasta **licenses**.

## Executar durante o desenvolvimento

Com o SDK do .NET 10 instalado, abra o PowerShell nesta pasta e execute:

```powershell
dotnet build WindowsPerformanceManager.sln
dotnet run --project src/WPM.App/WPM.App.csproj
```

No Visual Studio 2026 com **Desenvolvimento para desktop com .NET**, abra `WindowsPerformanceManager.sln` e pressione F5.

## Publicar para Windows x64

```powershell
dotnet publish src/WPM.App/WPM.App.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o artifacts/v0.8.0-win-x64
```

Abra `artifacts/v0.8.0-win-x64/DiagnosticoManutencao.exe`. Para mover o aplicativo, copie a pasta de publicação completa.
