#include "SOrchestratorChip.h"

#include "OrchestratorStatus.h"
#include "OrchestratorUI.h"
#include "Styling/AppStyle.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "AgentOrchestrator"

void SOrchestratorChip::Construct(const FArguments& InArgs, TSharedRef<FOrchestratorStatus> InStatus)
{
	Status = InStatus;
	OpenPanel = InArgs._OnOpenPanel;

	ChildSlot
	[
		SNew(SHorizontalBox)
		+ SHorizontalBox::Slot()
		.AutoWidth()
		.VAlign(VAlign_Center)
		[
			SNew(SButton)
			.ButtonStyle(FAppStyle::Get(), "SimpleButton")
			.ToolTipText(this, &SOrchestratorChip::GetSummaryToolTip)
			.OnClicked_Lambda([this]()
			{
				OpenPanel.ExecuteIfBound();
				return FReply::Handled();
			})
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.VAlign(VAlign_Center)
				.Padding(0.f, 0.f, 6.f, 0.f)
				[
					SNew(SBox)
					.WidthOverride(DOT_SIZE)
					.HeightOverride(DOT_SIZE)
					[
						SNew(SImage)
						.Image(FAppStyle::GetBrush("Icons.FilledCircle"))
						.ColorAndOpacity(this, &SOrchestratorChip::GetDotColor)
					]
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.VAlign(VAlign_Center)
				[
					SNew(SBox)
					.MaxDesiredWidth(SUMMARY_MAX_WIDTH)
					[
						SNew(STextBlock)
						.Text(this, &SOrchestratorChip::GetSummaryText)
						.OverflowPolicy(ETextOverflowPolicy::Ellipsis)
					]
				]
			]
		]
		+ SHorizontalBox::Slot()
		.AutoWidth()
		.VAlign(VAlign_Center)
		.Padding(4.f, 0.f, 0.f, 0.f)
		[
			SNew(SButton)
			.ButtonStyle(FAppStyle::Get(), "Button")
			.ToolTipText(this, &SOrchestratorChip::GetToggleToolTip)
			.IsEnabled_Lambda([this]() { return !Status->IsCommandRunning(); })
			.OnClicked(this, &SOrchestratorChip::OnToggleClicked)
			[
				SNew(STextBlock)
				.Text(this, &SOrchestratorChip::GetToggleText)
			]
		]
	];
}

FSlateColor SOrchestratorChip::GetDotColor() const
{
	return OrchestratorUI::OverallColor(*Status);
}

FText SOrchestratorChip::GetSummaryText() const
{
	const FOrchestratorSnapshot& Snapshot = Status->Get();
	if (!Snapshot.bValid)
	{
		return LOCTEXT("ChipNoFeed", "Agents: no orchestrator status yet");
	}
	if (Status->IsStale() && !Snapshot.Hold.bOn)
	{
		return FText::Format(LOCTEXT("ChipStale", "Agents: status {0} old"), FOrchestratorStatus::FormatAge(Snapshot.GeneratedAt));
	}
	return OrchestratorUI::SummaryText(Snapshot);
}

FText SOrchestratorChip::GetSummaryToolTip() const
{
	const FOrchestratorSnapshot& Snapshot = Status->Get();
	FTextBuilder Builder;
	if (Snapshot.Lane.IsSet())
	{
		const FOrchestratorGrant& Lane = *Snapshot.Lane;
		Builder.AppendLineFormat(LOCTEXT("TipLane", "Who has the editor: {0}"), OrchestratorUI::GrantName(Lane));
		Builder.AppendLineFormat(LOCTEXT("TipLaneActivity", "{0}, held {1}"), OrchestratorUI::GrantActivity(Lane), FOrchestratorStatus::FormatAge(Lane.AcquiredAt));
		if (!Lane.Purpose.IsEmpty())
		{
			Builder.AppendLine(FText::FromString(Lane.Purpose));
		}
		if (Lane.TurnDueAt > 0.0)
		{
			Builder.AppendLine(OrchestratorUI::TurnText(Lane));
		}
	}
	else
	{
		Builder.AppendLine(LOCTEXT("TipLaneFree", "Nobody has the editor"));
	}
	if (Snapshot.Queue.Num() > 0)
	{
		Builder.AppendLineFormat(LOCTEXT("TipQueue", "{0} queued"), Snapshot.Queue.Num());
	}
	for (const FString& Occupant : Snapshot.Occupants)
	{
		Builder.AppendLine(FText::FromString(Occupant));
	}
	for (const FOrchestratorEditor& Editor : Snapshot.Editors)
	{
		if (Editor.NotRespondingSince > 0.0)
		{
			Builder.AppendLineFormat(LOCTEXT("TipEditorWarning", "{0}: {1}"), OrchestratorUI::EditorTitle(Editor), OrchestratorUI::EditorWarning(Editor));
		}
	}
	Builder.AppendLine(LOCTEXT("TipOpen", "Click for the Agent Orchestrator panel."));
	return Builder.ToText();
}

FText SOrchestratorChip::GetToggleText() const
{
	if (Status->IsCommandRunning())
	{
		return LOCTEXT("ToggleBusy", "...");
	}
	if (Status->IsTakeArmed())
	{
		return FText::Format(LOCTEXT("ToggleConfirm", "Pause {0} {0}|plural(one=agent,other=agents)?"), Status->Get().Summary.TakePauses.Num());
	}
	return Status->Get().Hold.bOn ? LOCTEXT("ToggleRelease", "Resume agents") : LOCTEXT("ToggleTake", "Take editor");
}

FText SOrchestratorChip::GetToggleToolTip() const
{
	const FOrchestratorSnapshot& Snapshot = Status->Get();
	if (Snapshot.Hold.bOn)
	{
		return LOCTEXT("ToggleReleaseTip", "Give the editor back. Paused agents carry on where they stopped.");
	}
	if (Status->IsTakeArmed())
	{
		return FText::Format(LOCTEXT("ToggleConfirmTip", "Click again to pause {0}. It stops asking after a few seconds."), OrchestratorUI::TakePausesNames(Snapshot));
	}
	return Snapshot.Summary.TakePauses.Num() > 0
		? FText::Format(LOCTEXT("ToggleTakeAskTip", "Pause every agent at its next editor, PIE, build, test or p4 sync command until you resume them. {0} would stop for you, so this asks once."), OrchestratorUI::TakePausesNames(Snapshot))
		: LOCTEXT("ToggleTakeTip", "Pause every agent at its next editor, PIE, build, test or p4 sync command until you resume them. Nothing is killed mid-command.");
}

FReply SOrchestratorChip::OnToggleClicked()
{
	if (Status->Get().Hold.bOn)
	{
		Status->ReleaseEditor();
	}
	else
	{
		Status->RequestTakeEditor(FString());
	}
	return FReply::Handled();
}

#undef LOCTEXT_NAMESPACE
