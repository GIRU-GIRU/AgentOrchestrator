#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"

class FOrchestratorStatus;
class SEditableTextBox;
class SScrollBox;
class STextBlock;
class SVerticalBox;
struct FManagerAgent;
struct FOrchestratorAgent;

/**
 * The Agent Orchestrator tab. Its agents are Agent Manager's, read from the manager's live.json, with the orchestrator's own sessions
 * only when the manager is not running. Rows are rebuilt only when the set of agents or queue entries changes; everything inside a row
 * reads the latest state through attributes, so a click is never lost to a rebuild.
 */
class SOrchestratorPanel : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SOrchestratorPanel) {}
	SLATE_END_ARGS()

	/** The widgets Orch.SelfTest reads back, so it reports what is on screen and not what the feed says should be. */
	struct FSelfTestView
	{
		TSharedPtr<STextBlock> LaneFree;
		TSharedPtr<STextBlock> LaneName;
		TSharedPtr<STextBlock> LaneLine;
		TSharedPtr<STextBlock> LanePurpose;
		TSharedPtr<STextBlock> TurnLine;
		TSharedPtr<SVerticalBox> Occupants;
		TSharedPtr<SVerticalBox> Editors;
		TSharedPtr<SVerticalBox> Queue;
		TSharedPtr<SVerticalBox> Agents;
		TSharedPtr<SScrollBox> Scroll;
	};

	void Construct(const FArguments& InArgs, TSharedRef<FOrchestratorStatus> InStatus);
	virtual ~SOrchestratorPanel() override;

	FSelfTestView GetSelfTestView() const { return View; }
	bool IsAgentShown(const FOrchestratorAgent& Agent) const;
	bool IsAgentShown(const FManagerAgent& Agent) const;
	FText GetAgentsHeader() const;

private:
	static constexpr float SECTION_GAP = 12.f;
	static constexpr float ROW_GAP = 4.f;
	static constexpr float DOT_SIZE = 9.f;

	void OnStatusUpdated();
	FString ComputeStructureKey() const;
	void RebuildRows();

	TSharedRef<SWidget> MakeHoldCard();
	TSharedRef<SWidget> MakeLaneCard();
	TSharedRef<SWidget> MakeFooter();
	TSharedRef<SWidget> MakeHeader(const TAttribute<FText>& Text);
	TSharedRef<SWidget> MakeDot(const TAttribute<FSlateColor>& Color);
	TSharedRef<SWidget> MakeCopyButton(const TAttribute<FString>& Value, const FText& ToolTip);
	TSharedRef<SWidget> MakeAgentRow(const FString& Owner);
	TSharedRef<SWidget> MakeManagerAgentRow(const FString& Key);
	TSharedRef<SWidget> MakeQueueRow(const FString& Id);
	TSharedRef<SWidget> MakeEditorRow(uint32 Pid);

	TSharedPtr<FOrchestratorStatus> Status;
	FDelegateHandle UpdatedHandle;
	FSelfTestView View;
	TSharedPtr<SEditableTextBox> ReasonBox;
	FString StructureKey;
	bool bShowIdle = false;
};
