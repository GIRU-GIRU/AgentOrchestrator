#include "SOrchestratorPanel.h"

#include "HAL/PlatformApplicationMisc.h"
#include "HAL/PlatformProcess.h"
#include "OrchestratorStatus.h"
#include "OrchestratorUI.h"
#include "Styling/AppStyle.h"
#include "Styling/CoreStyle.h"
#include "Styling/StyleColors.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Input/SCheckBox.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "AgentOrchestrator"

namespace OrchestratorPanelPrivate
{
	FSlateFontInfo TitleFont()
	{
		return FAppStyle::GetFontStyle("NormalFontBold");
	}

	FSlateFontInfo SmallFont()
	{
		return FAppStyle::GetFontStyle("SmallFont");
	}

	FSlateFontInfo StateFont()
	{
		return FCoreStyle::GetDefaultFontStyle("Bold", 8);
	}

	FSlateFontInfo HeadlineFont()
	{
		return FCoreStyle::GetDefaultFontStyle("Bold", 12);
	}

	bool IsAgentIdle(const FOrchestratorAgent& Agent)
	{
		return !Agent.bWorking && !Agent.bHoldsLane && !Agent.bPaused && Agent.Grants.Num() == 0;
	}
}

void SOrchestratorPanel::Construct(const FArguments& InArgs, TSharedRef<FOrchestratorStatus> InStatus)
{
	Status = InStatus;
	UpdatedHandle = Status->OnUpdated.AddSP(this, &SOrchestratorPanel::OnStatusUpdated);

	ChildSlot
	[
		SNew(SBorder)
		.BorderImage(FAppStyle::GetBrush("Brushes.Panel"))
		.Padding(10.f)
		[
			SAssignNew(View.Scroll, SScrollBox)
			+ SScrollBox::Slot()
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()
				[
					MakeHoldCard()
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0.f, SECTION_GAP, 0.f, ROW_GAP)
				[
					MakeHeader(LOCTEXT("LaneHeader", "Who has the editor"))
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					MakeLaneCard()
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SAssignNew(View.Occupants, SVerticalBox)
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0.f, SECTION_GAP, 0.f, ROW_GAP)
				[
					MakeHeader(TAttribute<FText>::CreateLambda([this]()
					{
						return FText::Format(LOCTEXT("EditorsHeader", "Editors ({0})"), Status->Get().Editors.Num());
					}))
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SAssignNew(View.Editors, SVerticalBox)
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0.f, SECTION_GAP, 0.f, ROW_GAP)
				[
					MakeHeader(TAttribute<FText>::CreateLambda([this]()
					{
						return FText::Format(LOCTEXT("QueueHeader", "Queue ({0})"), Status->Get().Queue.Num());
					}))
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SAssignNew(View.Queue, SVerticalBox)
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0.f, SECTION_GAP, 0.f, ROW_GAP)
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
					[
						MakeHeader(TAttribute<FText>::CreateLambda([this]()
						{
							return GetAgentsHeader();
						}))
					]
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
					[
						SNew(SCheckBox)
						.IsChecked_Lambda([this]() { return bShowIdle ? ECheckBoxState::Checked : ECheckBoxState::Unchecked; })
						.OnCheckStateChanged_Lambda([this](ECheckBoxState State)
						{
							bShowIdle = State == ECheckBoxState::Checked;
							OnStatusUpdated();
						})
						[
							SNew(STextBlock)
							.Text_Lambda([this]()
							{
								if (Status->HasManager())
								{
									int32 Quiet = Status->GetManagerAgents().FilterByPredicate([](const FManagerAgent& Agent) { return Agent.IsQuiet() && !Agent.IsMasterAgent(); }).Num();
									return FText::Format(LOCTEXT("ShowQuiet", "Show idle and finished ({0})"), Quiet);
								}
								int32 Idle = Status->Get().Agents.FilterByPredicate([](const FOrchestratorAgent& Agent) { return OrchestratorPanelPrivate::IsAgentIdle(Agent); }).Num();
								return FText::Format(LOCTEXT("ShowIdle", "Show idle sessions ({0})"), Idle);
							})
							.Font(OrchestratorPanelPrivate::SmallFont())
						]
					]
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SAssignNew(View.Agents, SVerticalBox)
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0.f, SECTION_GAP, 0.f, 0.f)
				[
					MakeFooter()
				]
			]
		]
	];

	RebuildRows();
}

SOrchestratorPanel::~SOrchestratorPanel()
{
	if (Status.IsValid())
	{
		Status->OnUpdated.Remove(UpdatedHandle);
	}
}

void SOrchestratorPanel::OnStatusUpdated()
{
	if (ComputeStructureKey() != StructureKey)
	{
		RebuildRows();
	}
}

bool SOrchestratorPanel::IsAgentShown(const FOrchestratorAgent& Agent) const
{
	return bShowIdle || !OrchestratorPanelPrivate::IsAgentIdle(Agent);
}

bool SOrchestratorPanel::IsAgentShown(const FManagerAgent& Agent) const
{
	return bShowIdle || !Agent.IsQuiet() || Agent.IsMasterAgent();
}

FText SOrchestratorPanel::GetAgentsHeader() const
{
	if (Status->HasManager())
	{
		int32 Shown = Status->GetManagerAgents().FilterByPredicate([this](const FManagerAgent& Agent) { return IsAgentShown(Agent); }).Num();
		return FText::Format(LOCTEXT("ManagerAgentsHeader", "Agents ({0})"), Shown);
	}
	int32 Shown = Status->Get().Agents.FilterByPredicate([this](const FOrchestratorAgent& Agent) { return IsAgentShown(Agent); }).Num();
	return FText::Format(LOCTEXT("AgentsHeader", "Agent sessions ({0}) - open Agent Manager for what each one is doing"), Shown);
}

FString SOrchestratorPanel::ComputeStructureKey() const
{
	const FOrchestratorSnapshot& Snapshot = Status->Get();
	FString Key = FString::Join(Snapshot.Occupants, TEXT(";"));
	Key += TEXT("|");
	for (const FOrchestratorEditor& Editor : Snapshot.Editors)
	{
		Key += FString::Printf(TEXT("%u;"), Editor.Pid);
	}
	Key += TEXT("|");
	for (const FOrchestratorGrant& Grant : Snapshot.Queue)
	{
		Key += Grant.Id + TEXT(";");
	}
	Key += TEXT("|");
	if (Status->HasManager())
	{
		Key += TEXT("manager;");
		for (const FManagerAgent& Agent : Status->GetManagerAgents())
		{
			if (IsAgentShown(Agent))
			{
				Key += Agent.Key + TEXT(";");
			}
		}
		return Key;
	}
	for (const FOrchestratorAgent& Agent : Snapshot.Agents)
	{
		if (IsAgentShown(Agent))
		{
			Key += Agent.Owner + TEXT(";");
		}
	}
	return Key;
}

void SOrchestratorPanel::RebuildRows()
{
	const FOrchestratorSnapshot& Snapshot = Status->Get();
	StructureKey = ComputeStructureKey();

	View.Occupants->ClearChildren();
	for (const FString& Occupant : Snapshot.Occupants)
	{
		View.Occupants->AddSlot().AutoHeight().Padding(0.f, ROW_GAP, 0.f, 0.f)
		[
			SNew(STextBlock)
			.Text(FText::Format(LOCTEXT("Occupant", "Outside the orchestrator: {0}"), FText::FromString(Occupant)))
			.ColorAndOpacity(FStyleColors::Warning)
			.AutoWrapText(true)
		];
	}

	View.Editors->ClearChildren();
	for (const FOrchestratorEditor& Editor : Snapshot.Editors)
	{
		View.Editors->AddSlot().AutoHeight().Padding(0.f, 0.f, 0.f, ROW_GAP)
		[
			MakeEditorRow(Editor.Pid)
		];
	}
	if (Snapshot.Editors.Num() == 0)
	{
		View.Editors->AddSlot().AutoHeight()
		[
			SNew(STextBlock).Text(LOCTEXT("EditorsNone", "No editor seen yet.")).ColorAndOpacity(FSlateColor::UseSubduedForeground())
		];
	}

	View.Queue->ClearChildren();
	for (const FOrchestratorGrant& Grant : Snapshot.Queue)
	{
		View.Queue->AddSlot().AutoHeight().Padding(0.f, 0.f, 0.f, ROW_GAP)
		[
			MakeQueueRow(Grant.Id)
		];
	}
	if (Snapshot.Queue.Num() == 0)
	{
		View.Queue->AddSlot().AutoHeight()
		[
			SNew(STextBlock).Text(LOCTEXT("QueueEmpty", "Nobody is waiting.")).ColorAndOpacity(FSlateColor::UseSubduedForeground())
		];
	}

	View.Agents->ClearChildren();
	int32 Shown = 0;
	if (Status->HasManager())
	{
		for (const FManagerAgent& Agent : Status->GetManagerAgents())
		{
			if (IsAgentShown(Agent))
			{
				++Shown;
				View.Agents->AddSlot().AutoHeight().Padding(0.f, 0.f, 0.f, ROW_GAP)
				[
					MakeManagerAgentRow(Agent.Key)
				];
			}
		}
	}
	else
	{
		for (const FOrchestratorAgent& Agent : Snapshot.Agents)
		{
			if (IsAgentShown(Agent))
			{
				++Shown;
				View.Agents->AddSlot().AutoHeight().Padding(0.f, 0.f, 0.f, ROW_GAP)
				[
					MakeAgentRow(Agent.Owner)
				];
			}
		}
	}
	if (Shown == 0)
	{
		View.Agents->AddSlot().AutoHeight()
		[
			SNew(STextBlock).Text(LOCTEXT("AgentsNone", "No agent is working right now.")).ColorAndOpacity(FSlateColor::UseSubduedForeground())
		];
	}
}

TSharedRef<SWidget> SOrchestratorPanel::MakeHeader(const TAttribute<FText>& Text)
{
	return SNew(STextBlock)
		.Text(Text)
		.Font(OrchestratorPanelPrivate::TitleFont())
		.ColorAndOpacity(FSlateColor::UseForeground());
}

TSharedRef<SWidget> SOrchestratorPanel::MakeDot(const TAttribute<FSlateColor>& Color)
{
	return SNew(SBox)
		.WidthOverride(DOT_SIZE)
		.HeightOverride(DOT_SIZE)
		[
			SNew(SImage)
			.Image(FAppStyle::GetBrush("Icons.FilledCircle"))
			.ColorAndOpacity(Color)
		];
}

TSharedRef<SWidget> SOrchestratorPanel::MakeCopyButton(const TAttribute<FString>& Value, const FText& ToolTip)
{
	return SNew(SButton)
		.ButtonStyle(FAppStyle::Get(), "SimpleButton")
		.ToolTipText(ToolTip)
		.IsEnabled_Lambda([Value]() { return !Value.Get().IsEmpty(); })
		.OnClicked_Lambda([Value]()
		{
			FPlatformApplicationMisc::ClipboardCopy(*Value.Get());
			return FReply::Handled();
		})
		[
			SNew(SImage)
			.Image(FAppStyle::GetBrush("GenericCommands.Copy"))
			.ColorAndOpacity(FSlateColor::UseForeground())
		];
}

TSharedRef<SWidget> SOrchestratorPanel::MakeHoldCard()
{
	return SNew(SBorder)
		.BorderImage(FAppStyle::GetBrush("Brushes.Header"))
		.Padding(10.f)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight()
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0.f, 0.f, 8.f, 0.f)
				[
					MakeDot(TAttribute<FSlateColor>::CreateLambda([this]() { return OrchestratorUI::OverallColor(*Status); }))
				]
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
				[
					SNew(STextBlock)
					.Font(OrchestratorPanelPrivate::HeadlineFont())
					.Text_Lambda([this]()
					{
						const FOrchestratorSnapshot& Snapshot = Status->Get();
						if (Snapshot.Hold.bOn)
						{
							return OrchestratorUI::HoldSummary(Snapshot);
						}
						return LOCTEXT("HoldOffHeadline", "Agents may use the editor");
					})
				]
			]
			+ SVerticalBox::Slot().AutoHeight().Padding(0.f, 6.f, 0.f, 0.f)
			[
				SNew(STextBlock)
				.AutoWrapText(true)
				.ColorAndOpacity(FSlateColor::UseSubduedForeground())
				.Text_Lambda([this]()
				{
					const FOrchestratorSnapshot& Snapshot = Status->Get();
					if (!Snapshot.Hold.bOn)
					{
						return LOCTEXT("HoldOffBody", "Taking the editor pauses every agent at its next editor, PIE, build, test or p4 sync command; "
							"nothing is killed mid-command and they keep their place. Reading, editing code and single-file compiles carry on.");
					}
					const TArray<FString>& Names = Snapshot.Hold.PausedNames;
					FText Who = Names.Num() > 0 ? FText::FromString(FString::Join(Names, TEXT(", "))) : LOCTEXT("HoldNobody", "none yet");
					FText Note = Snapshot.Hold.Reason.IsEmpty() ? FText::GetEmpty() : FText::Format(LOCTEXT("HoldReasonNote", "\"{0}\" - "), FText::FromString(Snapshot.Hold.Reason));
					return FText::Format(LOCTEXT("HoldOnBody", "{0}Waiting for you: {1}. They resume where they stopped when you give the editor back."), Note, Who);
				})
			]
			+ SVerticalBox::Slot().AutoHeight().Padding(0.f, 8.f, 0.f, 0.f)
			[
				SAssignNew(ReasonBox, SEditableTextBox)
				.HintText(LOCTEXT("ReasonHint", "What are you doing? (optional - agents are told)"))
				.Visibility_Lambda([this]() { return Status->Get().Hold.bOn ? EVisibility::Collapsed : EVisibility::Visible; })
			]
			+ SVerticalBox::Slot().AutoHeight().Padding(0.f, 8.f, 0.f, 0.f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					SNew(SButton)
					.ButtonStyle(FAppStyle::Get(), "PrimaryButton")
					.IsEnabled_Lambda([this]() { return !Status->IsCommandRunning(); })
					.ContentPadding(FMargin(14.f, 4.f))
					.OnClicked_Lambda([this]()
					{
						if (Status->Get().Hold.bOn)
						{
							Status->ReleaseEditor();
						}
						else if (Status->RequestTakeEditor(ReasonBox->GetText().ToString()))
						{
							ReasonBox->SetText(FText::GetEmpty());
						}
						return FReply::Handled();
					})
					[
						SNew(STextBlock)
						.Text_Lambda([this]()
						{
							if (Status->IsCommandRunning())
							{
								return LOCTEXT("HoldWorking", "Working...");
							}
							if (Status->IsTakeArmed())
							{
								return FText::Format(LOCTEXT("HoldConfirm", "Click again to pause {0}"), OrchestratorUI::TakePausesNames(Status->Get()));
							}
							return Status->Get().Hold.bOn ? LOCTEXT("HoldRelease", "Give the editor back (resume agents)") : LOCTEXT("HoldTake", "Take the editor (pause agents)");
						})
					]
				]
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center).Padding(12.f, 0.f, 0.f, 0.f)
				[
					SNew(SCheckBox)
					.ToolTipText(LOCTEXT("StopPieTip", "When an agent holds the editor and its PIE is running, end that PIE as you take the editor. The agent is told to start it again later."))
					.IsChecked_Lambda([this]() { return Status->ShouldStopAgentPieOnTake() ? ECheckBoxState::Checked : ECheckBoxState::Unchecked; })
					.OnCheckStateChanged_Lambda([this](ECheckBoxState State) { Status->SetStopAgentPieOnTake(State == ECheckBoxState::Checked); })
					[
						SNew(STextBlock).Text(LOCTEXT("StopPie", "Stop the agent's PIE when I take the editor"))
					]
				]
			]
		];
}

TSharedRef<SWidget> SOrchestratorPanel::MakeLaneCard()
{
	auto HasLane = [this]() { return Status->Get().Lane.IsSet(); };
	return SNew(SBorder)
		.BorderImage(FAppStyle::GetBrush("Brushes.Recessed"))
		.Padding(8.f)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight()
			[
				SAssignNew(View.LaneFree, STextBlock)
				.Visibility_Lambda([HasLane]() { return HasLane() ? EVisibility::Collapsed : EVisibility::Visible; })
				.Text(LOCTEXT("LaneFree", "Nobody - no agent has the editor, a build or PIE."))
				.ColorAndOpacity(FSlateColor::UseSubduedForeground())
			]
			+ SVerticalBox::Slot().AutoHeight()
			[
				SNew(SHorizontalBox)
				.Visibility_Lambda([HasLane]() { return HasLane() ? EVisibility::Visible : EVisibility::Collapsed; })
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Top).Padding(0.f, 4.f, 8.f, 0.f)
				[
					MakeDot(TAttribute<FSlateColor>::CreateLambda([this]()
					{
						const FOrchestratorSnapshot& Snapshot = Status->Get();
						return Snapshot.Lane.IsSet() ? OrchestratorUI::AgentStateColor(Snapshot.Lane->Liveness) : FSlateColor(FStyleColors::AccentGray);
					}))
				]
				+ SHorizontalBox::Slot().FillWidth(1.f)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight()
					[
						SAssignNew(View.LaneName, STextBlock)
						.Font(OrchestratorPanelPrivate::TitleFont())
						.Text_Lambda([this]()
						{
							const FOrchestratorSnapshot& Snapshot = Status->Get();
							return Snapshot.Lane.IsSet() ? OrchestratorUI::GrantName(*Snapshot.Lane) : FText::GetEmpty();
						})
					]
					+ SVerticalBox::Slot().AutoHeight()
					[
						SAssignNew(View.LaneLine, STextBlock)
						.Text_Lambda([this]()
						{
							const FOrchestratorSnapshot& Snapshot = Status->Get();
							if (!Snapshot.Lane.IsSet())
							{
								return FText::GetEmpty();
							}
							const FOrchestratorGrant& Lane = *Snapshot.Lane;
							return FText::Format(LOCTEXT("LaneLine", "{0} - held {1}{2}"), OrchestratorUI::GrantActivity(Lane),
								FOrchestratorStatus::FormatAge(Lane.AcquiredAt),
								Snapshot.Hold.bOn ? LOCTEXT("LanePaused", " - paused by your hold") : FText::GetEmpty());
						})
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0.f, 2.f, 0.f, 0.f)
					[
						SAssignNew(View.LanePurpose, STextBlock)
						.AutoWrapText(true)
						.ColorAndOpacity(FSlateColor::UseSubduedForeground())
						.Text_Lambda([this]()
						{
							const FOrchestratorSnapshot& Snapshot = Status->Get();
							return Snapshot.Lane.IsSet() ? FText::FromString(Snapshot.Lane->Purpose) : FText::GetEmpty();
						})
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0.f, 2.f, 0.f, 0.f)
					[
						SAssignNew(View.TurnLine, STextBlock)
						.AutoWrapText(true)
						.Visibility_Lambda([this]()
						{
							const FOrchestratorSnapshot& Snapshot = Status->Get();
							return Snapshot.Lane.IsSet() && Snapshot.Lane->TurnDueAt > 0.0 ? EVisibility::Visible : EVisibility::Collapsed;
						})
						.ColorAndOpacity_Lambda([this]()
						{
							const FOrchestratorSnapshot& Snapshot = Status->Get();
							bool bIsOver = Snapshot.Lane.IsSet() && Snapshot.Lane->TurnDueAt > 0.0 && FOrchestratorStatus::NowUnix() >= Snapshot.Lane->TurnDueAt;
							return bIsOver ? FStyleColors::Warning : FSlateColor::UseForeground();
						})
						.Text_Lambda([this]()
						{
							const FOrchestratorSnapshot& Snapshot = Status->Get();
							return Snapshot.Lane.IsSet() ? OrchestratorUI::TurnText(*Snapshot.Lane) : FText::GetEmpty();
						})
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0.f, 2.f, 0.f, 0.f)
					[
						SNew(STextBlock)
						.Font(OrchestratorPanelPrivate::SmallFont())
						.ColorAndOpacity(FSlateColor::UseSubduedForeground())
						.OverflowPolicy(ETextOverflowPolicy::Ellipsis)
						.Text_Lambda([this]()
						{
							const FOrchestratorSnapshot& Snapshot = Status->Get();
							if (!Snapshot.Lane.IsSet())
							{
								return FText::GetEmpty();
							}
							const FOrchestratorAgent* Agent = Snapshot.FindAgent(Snapshot.Lane->Owner);
							if (Agent && !Agent->OpenTool.IsEmpty())
							{
								return FText::Format(LOCTEXT("LaneNow", "Running now: {0} ({1})"), FText::FromString(Agent->OpenTool), FOrchestratorStatus::FormatAge(Agent->OpenSince));
							}
							return Snapshot.Lane->LastCommand.IsEmpty() ? FText::GetEmpty()
								: FText::Format(LOCTEXT("LaneLast", "Last command: {0}"), FText::FromString(Snapshot.Lane->LastCommand.Replace(TEXT("\n"), TEXT(" "))));
						})
					]
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Top)
				[
					MakeCopyButton(TAttribute<FString>::CreateLambda([this]()
					{
						const FOrchestratorSnapshot& Snapshot = Status->Get();
						return Snapshot.Lane.IsSet() ? Snapshot.Lane->SessionId : FString();
					}), LOCTEXT("CopyLaneSession", "Copy this agent's session id (claude --resume <id>)"))
				]
			]
		];
}

TSharedRef<SWidget> SOrchestratorPanel::MakeAgentRow(const FString& Owner)
{
	auto Find = [this, Owner]() { return Status->Get().FindAgent(Owner); };
	return SNew(SBorder)
		.BorderImage(FAppStyle::GetBrush("Brushes.Recessed"))
		.Padding(FMargin(8.f, 5.f))
		.ToolTipText_Lambda([Find]()
		{
			const FOrchestratorAgent* Agent = Find();
			if (!Agent)
			{
				return FText::GetEmpty();
			}
			return FText::Format(LOCTEXT("AgentTip", "Owner: {0}\nSession: {1}\nWorking directory: {2}\nTranscript: {3}"),
				FText::FromString(Agent->Owner), FText::FromString(Agent->SessionId), FText::FromString(Agent->Cwd), FText::FromString(Agent->Transcript));
		})
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Top).Padding(0.f, 4.f, 8.f, 0.f)
			[
				MakeDot(TAttribute<FSlateColor>::CreateLambda([Find]()
				{
					const FOrchestratorAgent* Agent = Find();
					return Agent ? OrchestratorUI::AgentStateColor(Agent->State) : FSlateColor(FStyleColors::AccentGray);
				}))
			]
			+ SHorizontalBox::Slot().FillWidth(1.f)
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().FillWidth(1.f)
					[
						SNew(STextBlock)
						.Font(OrchestratorPanelPrivate::TitleFont())
						.OverflowPolicy(ETextOverflowPolicy::Ellipsis)
						.Text_Lambda([Find, Owner]()
						{
							const FOrchestratorAgent* Agent = Find();
							return FText::FromString(Agent ? Agent->DisplayName : Owner);
						})
					]
					+ SHorizontalBox::Slot().AutoWidth().Padding(8.f, 0.f, 0.f, 0.f)
					[
						SNew(STextBlock)
						.Font(OrchestratorPanelPrivate::SmallFont())
						.ColorAndOpacity_Lambda([Find]()
						{
							const FOrchestratorAgent* Agent = Find();
							return Agent && Agent->bPaused ? FStyleColors::AccentBlue : FStyleColors::AccentOrange;
						})
						.Text_Lambda([Find]()
						{
							const FOrchestratorAgent* Agent = Find();
							if (!Agent)
							{
								return FText::GetEmpty();
							}
							TArray<FText> Badges;
							if (Agent->bHoldsLane)
							{
								Badges.Add(LOCTEXT("BadgeLane", "HAS THE EDITOR"));
							}
							if (Agent->bPaused)
							{
								Badges.Add(LOCTEXT("BadgePaused", "PAUSED"));
							}
							if (Agent->bWaitingOnUser)
							{
								Badges.Add(LOCTEXT("BadgeAsking", "WAITING FOR ITS USER"));
							}
							return FText::Join(FText::FromString(TEXT("  ")), Badges);
						})
					]
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock)
					.Font(OrchestratorPanelPrivate::SmallFont())
					.ColorAndOpacity(FSlateColor::UseSubduedForeground())
					.OverflowPolicy(ETextOverflowPolicy::Ellipsis)
					.Text_Lambda([Find]()
					{
						const FOrchestratorAgent* Agent = Find();
						if (!Agent)
						{
							return LOCTEXT("AgentGone", "gone");
						}
						FText Line = FText::Format(LOCTEXT("AgentLine", "{0} - active {1} ago"), FText::FromString(Agent->StateText), FOrchestratorStatus::FormatAge(Agent->LastActive));
						if (!Agent->OpenTool.IsEmpty())
						{
							Line = FText::Format(LOCTEXT("AgentLineTool", "{0} - running {1} ({2})"), Line, FText::FromString(Agent->OpenTool), FOrchestratorStatus::FormatAge(Agent->OpenSince));
						}
						return Line;
					})
				]
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Top)
			[
				MakeCopyButton(TAttribute<FString>::CreateLambda([Find]()
				{
					const FOrchestratorAgent* Agent = Find();
					return Agent ? Agent->SessionId : FString();
				}), LOCTEXT("CopySession", "Copy the session id (claude --resume <id>)"))
			]
		];
}

TSharedRef<SWidget> SOrchestratorPanel::MakeQueueRow(const FString& Id)
{
	auto Find = [this, Id]() { return Status->Get().FindQueued(Id); };
	return SNew(SBorder)
		.BorderImage(FAppStyle::GetBrush("Brushes.Recessed"))
		.Padding(FMargin(8.f, 5.f))
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight()
			[
				SNew(STextBlock)
				.Font(OrchestratorPanelPrivate::TitleFont())
				.Text_Lambda([Find]()
				{
					const FOrchestratorGrant* Grant = Find();
					if (!Grant)
					{
						return FText::GetEmpty();
					}
					return FText::Format(LOCTEXT("QueueLine", "#{0}  {1} - wants {2}, waiting {3}"), Grant->Position, OrchestratorUI::GrantName(*Grant),
						FText::FromString(Grant->Kind), FOrchestratorStatus::FormatAge(Grant->QueuedAt));
				})
			]
			+ SVerticalBox::Slot().AutoHeight()
			[
				SNew(STextBlock)
				.Font(OrchestratorPanelPrivate::SmallFont())
				.ColorAndOpacity(FSlateColor::UseSubduedForeground())
				.AutoWrapText(true)
				.Text_Lambda([Find]()
				{
					const FOrchestratorGrant* Grant = Find();
					return Grant ? FText::FromString(Grant->Purpose) : FText::GetEmpty();
				})
			]
		];
}

/** What Agent Manager's card says, in its colours: the number, the name, which kind, the state word and the reason under it. */
TSharedRef<SWidget> SOrchestratorPanel::MakeManagerAgentRow(const FString& Key)
{
	auto Find = [this, Key]() { return Status->GetManagerAgents().FindByPredicate([&Key](const FManagerAgent& Agent) { return Agent.Key == Key; }); };
	return SNew(SBorder)
		.BorderImage(FAppStyle::GetBrush("Brushes.Recessed"))
		.Padding(FMargin(8.f, 5.f))
		.ToolTipText_Lambda([Find]()
		{
			const FManagerAgent* Agent = Find();
			return Agent ? FText::Format(LOCTEXT("ManagerAgentTip", "{0} session {1}\n{2}: {3}"), FText::FromString(Agent->Kind), FText::FromString(Agent->SessionId()),
				FText::FromString(Agent->State), FText::FromString(Agent->Reason)) : FText::GetEmpty();
		})
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Top).Padding(0.f, 4.f, 8.f, 0.f)
			[
				MakeDot(TAttribute<FSlateColor>::CreateLambda([Find]()
				{
					const FManagerAgent* Agent = Find();
					return Agent ? OrchestratorUI::ManagerStateColor(Agent->State) : FSlateColor(FStyleColors::AccentGray);
				}))
			]
			+ SHorizontalBox::Slot().FillWidth(1.f)
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0.f, 0.f, 6.f, 0.f)
					[
						SNew(STextBlock)
						.Font(OrchestratorPanelPrivate::SmallFont())
						.ColorAndOpacity(FSlateColor::UseSubduedForeground())
						.Text_Lambda([Find]()
						{
							const FManagerAgent* Agent = Find();
							return Agent ? FText::FromString(FString::Printf(TEXT("%02d"), Agent->Number)) : FText::GetEmpty();
						})
					]
					+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
					[
						SNew(STextBlock)
						.Font(OrchestratorPanelPrivate::TitleFont())
						.OverflowPolicy(ETextOverflowPolicy::Ellipsis)
						.ColorAndOpacity_Lambda([Find]()
						{
							const FManagerAgent* Agent = Find();
							return Agent && Agent->IsMasterAgent() ? OrchestratorUI::MasterAgentColor() : FSlateColor::UseForeground();
						})
						.Text_Lambda([Find, Key]()
						{
							const FManagerAgent* Agent = Find();
							return FText::FromString(Agent ? Agent->Name : Key);
						})
					]
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(8.f, 0.f, 0.f, 0.f)
					[
						SNew(STextBlock)
						.Font(OrchestratorPanelPrivate::SmallFont())
						.ColorAndOpacity_Lambda([Find]()
						{
							const FManagerAgent* Agent = Find();
							return Agent ? OrchestratorUI::KindColor(Agent->Kind) : FSlateColor::UseSubduedForeground();
						})
						.Text_Lambda([Find]()
						{
							const FManagerAgent* Agent = Find();
							return Agent ? FText::FromString(Agent->Kind) : FText::GetEmpty();
						})
					]
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(10.f, 0.f, 0.f, 0.f)
					[
						SNew(STextBlock)
						.Font(OrchestratorPanelPrivate::StateFont())
						.ColorAndOpacity_Lambda([Find]()
						{
							const FManagerAgent* Agent = Find();
							return Agent ? OrchestratorUI::ManagerStateColor(Agent->State) : FSlateColor(FStyleColors::AccentGray);
						})
						.Text_Lambda([Find]()
						{
							const FManagerAgent* Agent = Find();
							return Agent ? OrchestratorUI::ManagerStateWord(*Agent) : FText::GetEmpty();
						})
					]
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock)
					.Font(OrchestratorPanelPrivate::SmallFont())
					.ColorAndOpacity(FSlateColor::UseSubduedForeground())
					.OverflowPolicy(ETextOverflowPolicy::Ellipsis)
					.Text_Lambda([Find]()
					{
						const FManagerAgent* Agent = Find();
						return Agent ? FText::FromString(Agent->Reason) : LOCTEXT("ManagerAgentGone", "gone");
					})
				]
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Top)
			[
				MakeCopyButton(TAttribute<FString>::CreateLambda([Find]()
				{
					const FManagerAgent* Agent = Find();
					return Agent ? Agent->SessionId() : FString();
				}), LOCTEXT("CopyManagerSession", "Copy the session id (claude --resume <id> or codex resume <id>)"))
			]
		];
}

TSharedRef<SWidget> SOrchestratorPanel::MakeEditorRow(uint32 Pid)
{
	auto Find = [this, Pid]() { return Status->Get().FindEditor(Pid); };
	return SNew(SBorder)
		.BorderImage(FAppStyle::GetBrush("Brushes.Recessed"))
		.Padding(FMargin(8.f, 5.f))
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Top).Padding(0.f, 4.f, 8.f, 0.f)
			[
				MakeDot(TAttribute<FSlateColor>::CreateLambda([Find]()
				{
					const FOrchestratorEditor* Editor = Find();
					return Editor ? OrchestratorUI::EditorColor(*Editor) : FSlateColor(FStyleColors::AccentGray);
				}))
			]
			+ SHorizontalBox::Slot().FillWidth(1.f)
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock)
					.Font(OrchestratorPanelPrivate::TitleFont())
					.Text_Lambda([Find]()
					{
						const FOrchestratorEditor* Editor = Find();
						return Editor ? OrchestratorUI::EditorTitle(*Editor) : FText::GetEmpty();
					})
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock)
					.Font(OrchestratorPanelPrivate::SmallFont())
					.ColorAndOpacity(FSlateColor::UseSubduedForeground())
					.OverflowPolicy(ETextOverflowPolicy::Ellipsis)
					.Text_Lambda([Find]()
					{
						const FOrchestratorEditor* Editor = Find();
						return Editor ? OrchestratorUI::EditorDetail(*Editor) : FText::GetEmpty();
					})
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock)
					.AutoWrapText(true)
					.ColorAndOpacity(FStyleColors::Warning)
					.Visibility_Lambda([Find]()
					{
						const FOrchestratorEditor* Editor = Find();
						return Editor && Editor->NotRespondingSince > 0.0 ? EVisibility::Visible : EVisibility::Collapsed;
					})
					.Text_Lambda([Find]()
					{
						const FOrchestratorEditor* Editor = Find();
						return Editor ? OrchestratorUI::EditorWarning(*Editor) : FText::GetEmpty();
					})
				]
			]
		];
}

TSharedRef<SWidget> SOrchestratorPanel::MakeFooter()
{
	return SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
		[
			SNew(STextBlock)
			.Font(OrchestratorPanelPrivate::SmallFont())
			.ColorAndOpacity_Lambda([this]() { return Status->IsStale() ? FStyleColors::Warning : FSlateColor::UseSubduedForeground(); })
			.Text_Lambda([this]()
			{
				const FOrchestratorSnapshot& Snapshot = Status->Get();
				if (!Snapshot.bValid)
				{
					return LOCTEXT("FooterNoFeed", "No status yet - Saved/Orchestration/orch-status.json has not been written.");
				}
				return FText::Format(LOCTEXT("FooterLine", "Updated {0} ago - orchestrator daemon {1}{2}"), FOrchestratorStatus::FormatAge(Snapshot.GeneratedAt),
					Snapshot.bDaemonAlive ? LOCTEXT("DaemonUp", "running") : LOCTEXT("DaemonDown", "not running"),
					Status->GetLastError().IsEmpty() ? FText::GetEmpty() : FText::Format(LOCTEXT("FooterError", " - last command failed: {0}"), FText::FromString(Status->GetLastError().Left(200))));
			})
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(6.f, 0.f, 0.f, 0.f)
		[
			SNew(SButton)
			.Text(LOCTEXT("Refresh", "Refresh"))
			.IsEnabled_Lambda([this]() { return !Status->IsCommandRunning(); })
			.OnClicked_Lambda([this]()
			{
				Status->RefreshNow();
				return FReply::Handled();
			})
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(6.f, 0.f, 0.f, 0.f)
		[
			SNew(SButton)
			.Text(LOCTEXT("OpenJournal", "Open journal"))
			.ToolTipText(LOCTEXT("OpenJournalTip", "orch-events.log: every grant, release, cleanup and hold"))
			.OnClicked_Lambda([this]()
			{
				FPlatformProcess::LaunchFileInDefaultExternalApplication(*Status->GetJournalPath());
				return FReply::Handled();
			})
		];
}

#undef LOCTEXT_NAMESPACE
