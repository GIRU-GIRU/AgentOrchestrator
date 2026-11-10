#include "OrchestratorSelfTest.h"

#include "Dom/JsonObject.h"
#include "Framework/Application/SlateApplication.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "ImageUtils.h"
#include "Layout/WidgetPath.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "OrchestratorStatus.h"
#include "OrchestratorUI.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "SOrchestratorChip.h"
#include "SOrchestratorPanel.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

namespace OrchestratorSelfTestPrivate
{
	static constexpr const TCHAR* REPORT_FILE = TEXT("selftest.json");
	static constexpr const TCHAR* PANEL_PNG = TEXT("panel.png");
	static constexpr const TCHAR* CHIP_PNG = TEXT("chip.png");
	static constexpr const TCHAR* EXAMPLE_ORCH_ARGS = TEXT("status --history 0");
	static constexpr const TCHAR* EXEC_PROCESS_FLAGS = TEXT("NORMAL_PRIORITY_CLASS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW");
	static constexpr const TCHAR* EXEC_PROCESS_FLAGS_SOURCE = TEXT("FWindowsPlatformProcess::ExecProcess in the engine source; the plugin sets none of them and cannot read them at run time, so childConsole below is the measurement");
	static constexpr const TCHAR* CONSOLE_PROBE_PARAMS = TEXT("/d /s /c \"python -c \"import ctypes as c;h=c.windll.kernel32.GetConsoleWindow();print('visible' if h and c.windll.user32.IsWindowVisible(h) else 'hidden')\"\"");
	static constexpr const TCHAR* CONSOLE_HIDDEN = TEXT("hidden");
	static constexpr double TAKE_CLOCK_START = 1000.0;

	void CollectTexts(const TSharedRef<SWidget>& Widget, TArray<FString>& Out)
	{
		if (!Widget->GetVisibility().IsVisible())
		{
			return;
		}
		if (Widget->GetType() == FName(TEXT("STextBlock")))
		{
			FString Text = StaticCastSharedRef<STextBlock>(Widget)->GetText().ToString();
			if (!Text.IsEmpty())
			{
				Out.Add(MoveTemp(Text));
			}
		}
		FChildren* Children = Widget->GetChildren();
		for (int32 Index = 0; Index < Children->Num(); ++Index)
		{
			CollectTexts(Children->GetChildAt(Index), Out);
		}
	}

	TArray<TArray<FString>> RowTexts(const TSharedPtr<SVerticalBox>& Box)
	{
		TArray<TArray<FString>> Rows;
		FChildren* Children = Box->GetChildren();
		for (int32 Index = 0; Index < Children->Num(); ++Index)
		{
			CollectTexts(Children->GetChildAt(Index), Rows.AddDefaulted_GetRef());
		}
		return Rows;
	}

	FString Shown(const TSharedPtr<STextBlock>& Block)
	{
		return Block->GetVisibility().IsVisible() ? Block->GetText().ToString() : FString();
	}

	TArray<TSharedPtr<FJsonValue>> ToJson(const TArray<FString>& Strings)
	{
		TArray<TSharedPtr<FJsonValue>> Values;
		for (const FString& String : Strings)
		{
			Values.Add(MakeShared<FJsonValueString>(String));
		}
		return Values;
	}

	TArray<TSharedPtr<FJsonValue>> ToJson(const TArray<TArray<FString>>& Rows)
	{
		TArray<TSharedPtr<FJsonValue>> Values;
		for (const TArray<FString>& Row : Rows)
		{
			Values.Add(MakeShared<FJsonValueArray>(ToJson(Row)));
		}
		return Values;
	}

	bool IsOnScreen(const TSharedRef<SWidget>& Widget)
	{
		FWidgetPath Path;
		return FSlateApplication::Get().GeneratePathToWidgetUnchecked(Widget, Path) && Path.IsValid();
	}

	TSharedRef<FJsonObject> CheckRows(const TCHAR* Section, const TArray<FString>& Wanted, const TArray<TArray<FString>>& Rows, TArray<FString>& Failures)
	{
		TArray<FString> Missing;
		for (const FString& Entry : Wanted)
		{
			bool bHasRow = Rows.ContainsByPredicate([&Entry](const TArray<FString>& Row)
			{
				return Row.ContainsByPredicate([&Entry](const FString& Text) { return Text.Contains(Entry); });
			});
			if (!bHasRow)
			{
				Missing.Add(Entry);
				Failures.Add(FString::Printf(TEXT("%s: no row for \"%s\""), Section, *Entry));
			}
		}
		TSharedRef<FJsonObject> Json = MakeShared<FJsonObject>();
		Json->SetNumberField(TEXT("feed"), Wanted.Num());
		Json->SetNumberField(TEXT("rows"), Rows.Num());
		Json->SetBoolField(TEXT("everyEntryHasARow"), Missing.Num() == 0);
		Json->SetArrayField(TEXT("missing"), ToJson(Missing));
		return Json;
	}

	const TCHAR* TakeClickName(EOrchestratorTakeClick Click)
	{
		switch (Click)
		{
		case EOrchestratorTakeClick::Take:
			return TEXT("take");
		case EOrchestratorTakeClick::Arm:
			return TEXT("arm");
		default:
			return TEXT("ignore");
		}
	}
}

FOrchestratorSelfTest::FOrchestratorSelfTest(TSharedRef<FOrchestratorStatus> InStatus, TFunction<TSharedPtr<SOrchestratorPanel>()> InFindPanel,
	TFunction<TSharedPtr<SOrchestratorChip>()> InFindChip, FSimpleDelegate InOpenPanel)
	: Status(InStatus)
	, FindPanel(MoveTemp(InFindPanel))
	, FindChip(MoveTemp(InFindChip))
	, OpenPanel(MoveTemp(InOpenPanel))
{
}

FOrchestratorSelfTest::~FOrchestratorSelfTest()
{
	if (TickerHandle.IsValid())
	{
		FTSTicker::GetCoreTicker().RemoveTicker(TickerHandle);
	}
}

void FOrchestratorSelfTest::Start()
{
	using namespace OrchestratorSelfTestPrivate;

	Report = MakeShared<FJsonObject>();
	Screenshots = MakeShared<FJsonObject>();
	Failures.Reset();
	Stage = EStage::Chip;
	FramesWaited = 0;
	for (const TCHAR* FileName : { REPORT_FILE, PANEL_PNG, CHIP_PNG })
	{
		IFileManager::Get().Delete(*(Status->GetEditorsDir() / FileName), false, false, true);
	}
	// Slate only draws off its own tick safely, and a console line can arrive from inside input handling.
	TickerHandle = FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateSP(this, &FOrchestratorSelfTest::Tick));
}

bool FOrchestratorSelfTest::Tick(float DeltaTime)
{
	using namespace OrchestratorSelfTestPrivate;

	switch (Stage)
	{
	case EStage::Chip:
	{
		// Before the tab opens: a panel docked over the level editor hides the toolbar.
		ReportChip();
		OpenPanel.ExecuteIfBound();
		PanelDeadline = FPlatformTime::Seconds() + PANEL_WAIT_SECONDS;
		Stage = EStage::WaitForPanel;
		return true;
	}
	case EStage::WaitForPanel:
	{
		TSharedPtr<SOrchestratorPanel> Panel = FindPanel();
		if (Panel.IsValid() && IsOnScreen(Panel.ToSharedRef()) && ++FramesWaited >= SETTLE_FRAMES)
		{
			FramesWaited = 0;
			Capture(Panel.ToSharedRef(), PANEL_PNG, true);
			ReportPanel(*Panel);
			Stage = EStage::Finish;
		}
		else if (FPlatformTime::Seconds() > PanelDeadline)
		{
			Failures.Add(TEXT("the panel never came on screen"));
			Stage = EStage::Finish;
		}
		return true;
	}
	default:
	{
		ReportTakeLogic();
		ReportRunOrch();
		WriteReport();
		TickerHandle.Reset();
		return false;
	}
	}
}

void FOrchestratorSelfTest::ReportChip()
{
	using namespace OrchestratorSelfTestPrivate;

	TSharedRef<FJsonObject> Json = MakeShared<FJsonObject>();
	Json->SetStringField(TEXT("dot"), OrchestratorUI::OverallDotName(*Status));
	Json->SetBoolField(TEXT("feedStale"), Status->IsStale());
	Json->SetNumberField(TEXT("feedAgeSeconds"), Status->SecondsSinceUpdate());
	TSharedPtr<SOrchestratorChip> Chip = FindChip();
	Json->SetBoolField(TEXT("built"), Chip.IsValid());
	if (Chip.IsValid())
	{
		TArray<FString> Texts;
		CollectTexts(Chip.ToSharedRef(), Texts);
		FString Summary = Chip->GetSummaryText().ToString();
		Json->SetStringField(TEXT("summary"), Summary);
		Json->SetStringField(TEXT("tooltip"), Chip->GetSummaryToolTip().ToString());
		Json->SetStringField(TEXT("toggle"), Chip->GetToggleText().ToString());
		Json->SetArrayField(TEXT("shownTexts"), ToJson(Texts));
		if (!Texts.Contains(Summary))
		{
			Failures.Add(TEXT("chip: its text block does not show its summary"));
		}
		Capture(Chip.ToSharedRef(), CHIP_PNG, false);
	}
	else
	{
		Failures.Add(TEXT("chip: the toolbar widget was never built"));
	}
	Report->SetObjectField(TEXT("chip"), Json);
}

void FOrchestratorSelfTest::ReportPanel(SOrchestratorPanel& Panel)
{
	using namespace OrchestratorSelfTestPrivate;

	const FOrchestratorSnapshot& Snapshot = Status->Get();
	SOrchestratorPanel::FSelfTestView View = Panel.GetSelfTestView();

	TSharedRef<FJsonObject> Json = MakeShared<FJsonObject>();
	TSharedRef<FJsonObject> Lane = MakeShared<FJsonObject>();
	Lane->SetBoolField(TEXT("free"), !Snapshot.Lane.IsSet());
	Lane->SetStringField(TEXT("freeLine"), Shown(View.LaneFree));
	Lane->SetStringField(TEXT("name"), Shown(View.LaneName));
	Lane->SetStringField(TEXT("line"), Shown(View.LaneLine));
	Lane->SetStringField(TEXT("purpose"), Shown(View.LanePurpose));
	Lane->SetStringField(TEXT("turnLine"), Shown(View.TurnLine));
	Json->SetObjectField(TEXT("lane"), Lane);

	TArray<FString> AllTexts;
	CollectTexts(StaticCastSharedRef<SWidget>(Panel.AsShared()), AllTexts);
	Json->SetArrayField(TEXT("shownTexts"), ToJson(AllTexts));

	FString AgentsHeader = Panel.GetAgentsHeader().ToString();
	Json->SetStringField(TEXT("agentsHeader"), AgentsHeader);
	Json->SetBoolField(TEXT("agentsFromManager"), Status->HasManager());
	if (!AllTexts.Contains(AgentsHeader))
	{
		Failures.Add(FString::Printf(TEXT("panel: \"%s\" is not on screen"), *AgentsHeader));
	}

	TArray<FString> WantedEditors;
	for (const FOrchestratorEditor& Editor : Snapshot.Editors)
	{
		WantedEditors.Add(OrchestratorUI::EditorTitle(Editor).ToString());
	}
	TArray<FString> WantedQueue;
	for (const FOrchestratorGrant& Grant : Snapshot.Queue)
	{
		WantedQueue.Add(OrchestratorUI::GrantName(Grant).ToString());
	}
	TArray<FString> WantedAgents;
	if (Status->HasManager())
	{
		for (const FManagerAgent& Agent : Status->GetManagerAgents())
		{
			if (Panel.IsAgentShown(Agent))
			{
				WantedAgents.Add(Agent.Name);
			}
		}
	}
	else
	{
		for (const FOrchestratorAgent& Agent : Snapshot.Agents)
		{
			if (Panel.IsAgentShown(Agent))
			{
				WantedAgents.Add(Agent.DisplayName);
			}
		}
	}

	TArray<TArray<FString>> EditorRows = RowTexts(View.Editors);
	TArray<TArray<FString>> QueueRows = RowTexts(View.Queue);
	TArray<TArray<FString>> AgentRows = RowTexts(View.Agents);
	TArray<TArray<FString>> OccupantRows = RowTexts(View.Occupants);
	Json->SetArrayField(TEXT("editorRows"), ToJson(EditorRows));
	Json->SetArrayField(TEXT("queueRows"), ToJson(QueueRows));
	Json->SetArrayField(TEXT("agentRows"), ToJson(AgentRows));
	Json->SetArrayField(TEXT("occupantRows"), ToJson(OccupantRows));

	TSharedRef<FJsonObject> RowChecks = MakeShared<FJsonObject>();
	RowChecks->SetObjectField(TEXT("editors"), CheckRows(TEXT("editors"), WantedEditors, EditorRows, Failures));
	RowChecks->SetObjectField(TEXT("queue"), CheckRows(TEXT("queue"), WantedQueue, QueueRows, Failures));
	RowChecks->SetObjectField(TEXT("agents"), CheckRows(TEXT("agents"), WantedAgents, AgentRows, Failures));
	RowChecks->SetObjectField(TEXT("occupants"), CheckRows(TEXT("occupants"), Snapshot.Occupants, OccupantRows, Failures));
	Json->SetObjectField(TEXT("rowChecks"), RowChecks);
	Report->SetObjectField(TEXT("panel"), Json);
}

void FOrchestratorSelfTest::ReportTakeLogic()
{
	using namespace OrchestratorSelfTestPrivate;

	struct FClick
	{
		double Now;
		EOrchestratorTakeClick Expected;
		double ArmedAtAfter;
	};
	struct FCase
	{
		const TCHAR* Name;
		bool bWouldPause;
		double ArmedAt;
		TArray<FClick> Clicks;
	};

	double Start = TAKE_CLOCK_START;
	double Confirm = FOrchestratorStatus::TAKE_CONFIRM_SECONDS;
	double Min = FOrchestratorStatus::TAKE_CONFIRM_MIN_SECONDS;
	double Inside = (Confirm + Min) * 0.5;
	double Late = Confirm + 1.0;
	double DoubleClick = Min * 0.5;
	TArray<FCase> Cases;
	Cases.Add({ TEXT("agent waiting, first click arms"), true, 0.0, { { Start, EOrchestratorTakeClick::Arm, Start } } });
	Cases.Add({ TEXT("second click inside the window takes"), true, Start, { { Start + Inside, EOrchestratorTakeClick::Take, 0.0 } } });
	Cases.Add({ TEXT("second click after the timeout only re-arms"), true, Start, { { Start + Late, EOrchestratorTakeClick::Arm, Start + Late } } });
	Cases.Add({ TEXT("a double-click does nothing"), true, 0.0,
		{ { Start, EOrchestratorTakeClick::Arm, Start }, { Start + DoubleClick, EOrchestratorTakeClick::Ignore, Start } } });
	Cases.Add({ TEXT("a real second click still takes after a double-click"), true, Start,
		{ { Start + DoubleClick, EOrchestratorTakeClick::Ignore, Start }, { Start + Inside, EOrchestratorTakeClick::Take, 0.0 } } });
	Cases.Add({ TEXT("nobody waiting, one click takes"), false, 0.0, { { Start, EOrchestratorTakeClick::Take, 0.0 } } });

	TArray<TSharedPtr<FJsonValue>> Results;
	for (const FCase& Case : Cases)
	{
		double ArmedAt = Case.ArmedAt;
		bool bPassed = true;
		TArray<FString> Got;
		for (const FClick& Click : Case.Clicks)
		{
			EOrchestratorTakeClick Decision = FOrchestratorStatus::DecideTakeClick(Case.bWouldPause, Click.Now, ArmedAt);
			Got.Add(TakeClickName(Decision));
			bPassed = bPassed && Decision == Click.Expected && ArmedAt == Click.ArmedAtAfter;
		}
		TSharedRef<FJsonObject> Result = MakeShared<FJsonObject>();
		Result->SetStringField(TEXT("case"), Case.Name);
		Result->SetStringField(TEXT("result"), bPassed ? TEXT("PASS") : TEXT("FAIL"));
		Result->SetArrayField(TEXT("got"), ToJson(Got));
		Results.Add(MakeShared<FJsonValueObject>(Result));
		if (!bPassed)
		{
			Failures.Add(FString::Printf(TEXT("take: %s"), Case.Name));
		}
	}
	Report->SetArrayField(TEXT("take"), Results);
	Report->SetBoolField(TEXT("holdOn"), Status->Get().Hold.bOn);
}

void FOrchestratorSelfTest::ReportRunOrch()
{
	using namespace OrchestratorSelfTestPrivate;

	TSharedRef<FJsonObject> Json = MakeShared<FJsonObject>();
	Json->SetStringField(TEXT("shell"), FOrchestratorStatus::GetOrchShell());
	Json->SetStringField(TEXT("params"), Status->GetOrchParams(EXAMPLE_ORCH_ARGS));
	Json->SetBoolField(TEXT("scriptExists"), FPaths::FileExists(Status->GetOrchScriptPath()));
	Json->SetStringField(TEXT("createFlags"), EXEC_PROCESS_FLAGS);
	Json->SetStringField(TEXT("createFlagsSource"), EXEC_PROCESS_FLAGS_SOURCE);

	int32 ReturnCode = -1;
	FString StdOut;
	FString StdErr;
	bool bLaunched = FPlatformProcess::ExecProcess(FOrchestratorStatus::GetOrchShell(), CONSOLE_PROBE_PARAMS, &ReturnCode, &StdOut, &StdErr);
	FString Console = StdOut.TrimStartAndEnd();
	Json->SetBoolField(TEXT("childLaunched"), bLaunched);
	Json->SetNumberField(TEXT("childExitCode"), ReturnCode);
	Json->SetStringField(TEXT("childConsole"), Console);
	Json->SetStringField(TEXT("childStdErr"), StdErr.TrimStartAndEnd());
	if (!FPaths::FileExists(Status->GetOrchScriptPath()))
	{
		Failures.Add(TEXT("runOrch: orch.cmd is not where RunOrch looks"));
	}
	if (Console != CONSOLE_HIDDEN)
	{
		Failures.Add(FString::Printf(TEXT("runOrch: a child of the same launch reports console \"%s\""), *Console));
	}
	Report->SetObjectField(TEXT("runOrch"), Json);
}

void FOrchestratorSelfTest::Capture(const TSharedRef<SWidget>& Widget, const TCHAR* FileName, bool bRequired)
{
	using namespace OrchestratorSelfTestPrivate;

	TSharedRef<FJsonObject> Shot = MakeShared<FJsonObject>();
	FString Why;
	FIntVector Size = FIntVector::ZeroValue;
	if (!IsOnScreen(Widget))
	{
		Why = TEXT("not on screen");
	}
	else
	{
		TArray<FColor> Pixels;
		if (!FSlateApplication::Get().TakeScreenshot(Widget, Pixels, Size))
		{
			Why = TEXT("TakeScreenshot failed");
		}
		else
		{
			// The back buffer's alpha is not meant to be seen, and a viewer would draw the panel see-through.
			for (FColor& Pixel : Pixels)
			{
				Pixel.A = MAX_uint8;
			}
			TArray64<uint8> Png;
			FImageUtils::PNGCompressImageArray(Size.X, Size.Y, TArrayView64<const FColor>(Pixels.GetData(), int64(Size.X) * Size.Y), Png);
			if (!FFileHelper::SaveArrayToFile(Png, *(Status->GetEditorsDir() / FileName)))
			{
				Why = TEXT("could not write the file");
			}
		}
	}
	Shot->SetBoolField(TEXT("ok"), Why.IsEmpty());
	Shot->SetStringField(TEXT("path"), Status->GetEditorsDir() / FileName);
	Shot->SetNumberField(TEXT("width"), Size.X);
	Shot->SetNumberField(TEXT("height"), Size.Y);
	Shot->SetStringField(TEXT("why"), Why);
	Screenshots->SetObjectField(FileName, Shot);
	if (bRequired && !Why.IsEmpty())
	{
		Failures.Add(FString::Printf(TEXT("%s: %s"), FileName, *Why));
	}
}

void FOrchestratorSelfTest::WriteReport()
{
	using namespace OrchestratorSelfTestPrivate;

	Report->SetStringField(TEXT("result"), Failures.Num() == 0 ? TEXT("PASS") : TEXT("FAIL"));
	Report->SetArrayField(TEXT("failures"), ToJson(Failures));
	Report->SetNumberField(TEXT("generatedAt"), FOrchestratorStatus::NowUnix());
	Report->SetNumberField(TEXT("editorPid"), FPlatformProcess::GetCurrentProcessId());
	Report->SetObjectField(TEXT("screenshots"), Screenshots);

	FString Text;
	TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Text);
	FJsonSerializer::Serialize(Report.ToSharedRef(), Writer);
	FString Path = Status->GetEditorsDir() / REPORT_FILE;
	FString Temporary = Path + TEXT(".tmp");
	if (FFileHelper::SaveStringToFile(Text, *Temporary, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
	{
		IFileManager::Get().Move(*Path, *Temporary, true, true, false, true);
	}
}
