#include "AgentOrchestratorModule.h"

#include "Framework/Application/SlateApplication.h"
#include "Framework/Docking/TabManager.h"
#include "Framework/Notifications/NotificationManager.h"
#include "Misc/CoreMisc.h"
#include "Modules/ModuleManager.h"
#include "OrchestratorSelfTest.h"
#include "OrchestratorStatus.h"
#include "SOrchestratorChip.h"
#include "SOrchestratorPanel.h"
#include "Styling/AppStyle.h"
#include "ToolMenus.h"
#include "Widgets/Docking/SDockTab.h"
#include "Widgets/Notifications/SNotificationList.h"
#include "WorkspaceMenuStructure.h"
#include "WorkspaceMenuStructureModule.h"

#define LOCTEXT_NAMESPACE "AgentOrchestrator"

namespace AgentOrchestratorModulePrivate
{
	static const FName PANEL_TAB_ID(TEXT("AgentOrchestrator"));
	static const FName TOOLBAR_MENU(TEXT("LevelEditor.LevelEditorToolBar.User"));
	static const FName TOOLBAR_SECTION(TEXT("AgentOrchestrator"));
	static constexpr const TCHAR* SELF_TEST_COMMAND = TEXT("Orch.SelfTest");
	static constexpr const TCHAR* SELF_TEST_HELP = TEXT("Opens the Agent Orchestrator panel and writes what it and the toolbar chip show to Saved/Orchestration/editors (selftest.json, written last, and PNGs). Sets no hold and changes no orchestrator state.");
}

void FAgentOrchestratorModule::StartupModule()
{
	if (!GIsEditor || IsRunningCommandlet() || IsRunningGame())
	{
		return;
	}

	Status = MakeShared<FOrchestratorStatus>();
	Status->OnFirstRead.AddRaw(this, &FAgentOrchestratorModule::WarnAboutLeftoverHold);
	Status->Start();

	FGlobalTabmanager::Get()->RegisterNomadTabSpawner(AgentOrchestratorModulePrivate::PANEL_TAB_ID,
		FOnSpawnTab::CreateRaw(this, &FAgentOrchestratorModule::SpawnPanelTab))
		.SetDisplayName(LOCTEXT("PanelTitle", "Agent Orchestrator"))
		.SetTooltipText(LOCTEXT("PanelTip", "Who holds the editor, the queue, every agent session, and the take / release toggle."))
		.SetGroup(WorkspaceMenu::GetMenuStructure().GetToolsCategory())
		.SetIcon(FSlateIcon(FAppStyle::GetAppStyleSetName(), "Icons.Lock"));

	UToolMenus::RegisterStartupCallback(FSimpleMulticastDelegate::FDelegate::CreateRaw(this, &FAgentOrchestratorModule::RegisterMenus));

	SelfTestCommand = MakeUnique<FAutoConsoleCommand>(AgentOrchestratorModulePrivate::SELF_TEST_COMMAND, AgentOrchestratorModulePrivate::SELF_TEST_HELP,
		FConsoleCommandDelegate::CreateRaw(this, &FAgentOrchestratorModule::RunSelfTest));
}

void FAgentOrchestratorModule::ShutdownModule()
{
	if (!Status.IsValid())
	{
		return;
	}
	SelfTestCommand.Reset();
	SelfTest.Reset();
	UToolMenus::UnRegisterStartupCallback(this);
	UToolMenus::UnregisterOwner(this);
	if (FSlateApplication::IsInitialized())
	{
		FGlobalTabmanager::Get()->UnregisterNomadTabSpawner(AgentOrchestratorModulePrivate::PANEL_TAB_ID);
	}
	Status->OnFirstRead.RemoveAll(this);
	Status->Stop();
	Status.Reset();
}

void FAgentOrchestratorModule::RegisterMenus()
{
	FToolMenuOwnerScoped OwnerScoped(this);
	UToolMenu* Toolbar = UToolMenus::Get()->ExtendMenu(AgentOrchestratorModulePrivate::TOOLBAR_MENU);
	FToolMenuSection& Section = Toolbar->FindOrAddSection(AgentOrchestratorModulePrivate::TOOLBAR_SECTION);
	TSharedRef<SOrchestratorChip> ChipWidget = SNew(SOrchestratorChip, Status.ToSharedRef())
		.OnOpenPanel(FSimpleDelegate::CreateRaw(this, &FAgentOrchestratorModule::OpenPanel));
	Chip = ChipWidget;
	Section.AddEntry(FToolMenuEntry::InitWidget(TEXT("AgentOrchestratorChip"), ChipWidget, FText::GetEmpty(), true, false));
}

TSharedRef<SDockTab> FAgentOrchestratorModule::SpawnPanelTab(const FSpawnTabArgs& Args)
{
	TSharedRef<SOrchestratorPanel> PanelWidget = SNew(SOrchestratorPanel, Status.ToSharedRef());
	Panel = PanelWidget;
	return SNew(SDockTab)
		.TabRole(ETabRole::NomadTab)
		[
			PanelWidget
		];
}

void FAgentOrchestratorModule::OpenPanel()
{
	FGlobalTabmanager::Get()->TryInvokeTab(AgentOrchestratorModulePrivate::PANEL_TAB_ID);
}

void FAgentOrchestratorModule::RunSelfTest()
{
	if (SelfTest.IsValid() && SelfTest->IsRunning())
	{
		return;
	}
	SelfTest = MakeShared<FOrchestratorSelfTest>(Status.ToSharedRef(),
		[this]() { return Panel.Pin(); },
		[this]() { return Chip.Pin(); },
		FSimpleDelegate::CreateRaw(this, &FAgentOrchestratorModule::OpenPanel));
	SelfTest->Start();
}

void FAgentOrchestratorModule::WarnAboutLeftoverHold()
{
	// A hold survives an editor restart (the orchestrator ends it only after ORPHANED_HOLD_SECONDS with no editor), so a relaunch can find agents still waiting on it.
	const FOrchestratorSnapshot& Snapshot = Status->Get();
	if (!Snapshot.Hold.bOn)
	{
		return;
	}
	TSharedPtr<TWeakPtr<SNotificationItem>> Item = MakeShared<TWeakPtr<SNotificationItem>>();
	TWeakPtr<FOrchestratorStatus> WeakStatus = Status;
	auto Close = [Item]()
	{
		if (TSharedPtr<SNotificationItem> Pinned = Item->Pin())
		{
			Pinned->ExpireAndFadeout();
		}
	};
	FNotificationInfo Info(FText::Format(LOCTEXT("LeftoverHold", "Agents are still paused: you took the editor {0} ago."), FOrchestratorStatus::FormatAge(Snapshot.Hold.Since)));
	Info.bFireAndForget = false;
	Info.ButtonDetails.Add(FNotificationButtonInfo(LOCTEXT("LeftoverRelease", "Resume agents"), FText::GetEmpty(),
		FSimpleDelegate::CreateLambda([WeakStatus, Close]()
		{
			if (TSharedPtr<FOrchestratorStatus> Pinned = WeakStatus.Pin())
			{
				Pinned->ReleaseEditor();
			}
			Close();
		}), SNotificationItem::CS_None));
	Info.ButtonDetails.Add(FNotificationButtonInfo(LOCTEXT("LeftoverKeep", "Keep the editor"), FText::GetEmpty(),
		FSimpleDelegate::CreateLambda([Close]() { Close(); }), SNotificationItem::CS_None));
	*Item = FSlateNotificationManager::Get().AddNotification(Info);
}

#undef LOCTEXT_NAMESPACE

IMPLEMENT_MODULE(FAgentOrchestratorModule, AgentOrchestrator)
