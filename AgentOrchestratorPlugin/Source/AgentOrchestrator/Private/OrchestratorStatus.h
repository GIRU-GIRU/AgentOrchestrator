#pragma once

#include "CoreMinimal.h"
#include "Containers/Ticker.h"

enum class EOrchestratorTakeClick : uint8
{
	Take,
	Arm,
	Ignore,
};

struct FOrchestratorGrant
{
	FString Id;
	FString Owner;
	FString DisplayName;
	FString SessionId;
	FString Liveness;
	FString Kind;
	FString Activity;
	FString Purpose;
	FString LastCommand;
	bool bImplicit = false;
	bool bOwnsPie = false;
	double QueuedAt = 0.0;
	double AcquiredAt = 0.0;
	double TurnDueAt = 0.0;
	double TurnBackAt = 0.0;
	int32 Position = 0;
	uint32 EditorPid = 0;
};

struct FOrchestratorAgent
{
	FString Owner;
	FString SessionId;
	FString DisplayName;
	FString Cwd;
	FString Transcript;
	FString State;
	FString StateText;
	FString OpenTool;
	double LastActive = 0.0;
	double OpenSince = 0.0;
	bool bWorking = false;
	bool bWaitingOnUser = false;
	bool bHoldsLane = false;
	bool bPaused = false;
	TArray<FString> Grants;
};

/**
 * One agent as Agent Manager shows it, read from the manager's own live.json, so the panel and the manager agree on who is
 * doing what.
 */
struct FManagerAgent
{
	FString Key;
	FString Name;
	FString Kind;
	FString State;
	FString Reason;
	int32 Number = 0;

	bool IsMasterAgent() const;
	bool IsQuiet() const;
	FString SessionId() const;
};

struct FOrchestratorTask
{
	FString Id;
	FString OwnerName;
	FString Title;
	FString State;
	FString NextStep;
	FString BlockedOn;
	FString Why;
	bool bOwnerAlive = false;
};

struct FOrchestratorHold
{
	bool bOn = false;
	FString Reason;
	double Since = 0.0;
	TArray<FString> PausedNames;
};

struct FOrchestratorEditor
{
	uint32 Pid = 0;
	FString Role;
	TOptional<bool> Pie;
	FString OnItName;
	double NotRespondingSince = 0.0;
	bool bFrozen = false;

	bool IsThisWindow() const;
};

struct FOrchestratorSummary
{
	FString State;
	FString Dot;
	FString Text;
	uint32 PiePid = 0;
	TArray<FString> TakePauses;
};

struct FOrchestratorSnapshot
{
	bool bValid = false;
	double GeneratedAt = 0.0;
	bool bDaemonAlive = false;
	FOrchestratorHold Hold;
	TOptional<FOrchestratorGrant> Lane;
	TArray<FOrchestratorGrant> Queue;
	TArray<FString> Occupants;
	TArray<FOrchestratorAgent> Agents;
	TArray<FOrchestratorTask> Tasks;
	int32 TasksLeftOver = 0;
	TArray<FOrchestratorEditor> Editors;
	FOrchestratorSummary Summary;

	const FOrchestratorAgent* FindAgent(const FString& Owner) const;
	const FOrchestratorGrant* FindQueued(const FString& Id) const;
	const FOrchestratorTask* FindTask(const FString& Id) const;
	const FOrchestratorEditor* FindEditor(uint32 Pid) const;
};

/**
 * Mirrors Saved/Orchestration/orch-status.json, which the orchestrator rewrites on every hook call and every daemon tick,
 * and runs orch.cmd for the editor's own actions. Every orchestrator rule stays there; this only reads, asks,
 * and says whether this editor's PIE is running.
 */
class FOrchestratorStatus : public TSharedFromThis<FOrchestratorStatus>
{
public:
	static constexpr double TAKE_CONFIRM_SECONDS = 6.0;
	static constexpr double TAKE_CONFIRM_MIN_SECONDS = 0.4;

	FOrchestratorStatus();
	~FOrchestratorStatus();

	DECLARE_MULTICAST_DELEGATE(FOnUpdated);
	FOnUpdated OnUpdated;

	DECLARE_MULTICAST_DELEGATE(FOnFirstRead);
	FOnFirstRead OnFirstRead;

	void Start();
	void Stop();

	const FOrchestratorSnapshot& Get() const { return Snapshot; }

	/** Agent Manager's running agents, in its own order. Empty when the manager is not running on this machine. */
	const TArray<FManagerAgent>& GetManagerAgents() const { return ManagerAgents; }
	bool HasManager() const;
	FString GetManagerLivePath() const;

	bool IsStale() const;
	double SecondsSinceUpdate() const;
	bool IsCommandRunning() const { return bCommandRunning; }
	const FString& GetLastError() const { return LastError; }

	/** Takes the editor at once when nobody would be paused. Otherwise the first click only arms it and returns false, so a stray click cannot pause anyone. */
	bool RequestTakeEditor(const FString& Reason);
	bool IsTakeArmed() const;

	/** What a click on Take editor does. InOutArmedAt is 0 while nothing is armed, on the same clock as Now. Pure, so Orch.SelfTest can drive it without a real hold. */
	static EOrchestratorTakeClick DecideTakeClick(bool bWouldPauseSomeone, double Now, double& InOutArmedAt);

	void ReleaseEditor();
	void RefreshNow();

	bool ShouldStopAgentPieOnTake() const { return bStopAgentPieOnTake; }
	void SetStopAgentPieOnTake(bool bValue);

	FString GetStatusFilePath() const;
	FString GetJournalPath() const;
	FString GetEditorsDir() const;
	FString GetOrchScriptPath() const;
	FString GetOrchParams(const FString& Args) const;
	static const TCHAR* GetOrchShell();

	static double NowUnix();
	static FText FormatAge(double UnixTime);
	static FText FormatDuration(double Seconds);

private:
	static constexpr float POLL_INTERVAL_SECONDS = 0.5f;
	static constexpr double STALE_AFTER_SECONDS = 30.0;
	static constexpr double KICK_INTERVAL_SECONDS = 30.0;
	static constexpr double PIE_REPORT_INTERVAL_SECONDS = 10.0;
	static constexpr double PIE_START_INPUT_WINDOW_SECONDS = 5.0;
	static constexpr double MANAGER_POLL_SECONDS = 2.0;
	/** The manager rewrites live.json every second while it runs, so a file this old means it is closed. */
	static constexpr double MANAGER_STALE_SECONDS = 30.0;

	bool Tick(float DeltaTime);
	void ReadStatusFile(bool bForce);
	bool Parse(const FString& Json, FOrchestratorSnapshot& Out) const;
	void ReadManagerFile();
	static bool ParseManager(const FString& Json, TArray<FManagerAgent>& Out);
	void TakeEditor(const FString& Reason);
	void RunOrch(const FString& Args, bool bReportFailure);
	void ReportPieState();
	void WritePieReport(bool bPie);
	void NotePieStart();
	void OnStartPie(bool bIsSimulating);
	FString GetPieReportPath() const;
	FString GetStateDir() const;

	FOrchestratorSnapshot Snapshot;
	TArray<FManagerAgent> ManagerAgents;
	FString ManagerSignature;
	FDateTime ManagerFileStamp;
	double LastManagerPoll = 0.0;
	bool bManagerWasLive = false;
	FTSTicker::FDelegateHandle TickerHandle;
	FDelegateHandle StartPieHandle;
	FDateTime LastFileStamp;
	int64 LastFileSize = -1;
	double LastKickTime = 0.0;
	double LastPieReportTime = 0.0;
	double TakeArmedAt = 0.0;
	double PieSince = 0.0;
	bool bPieByInput = false;
	bool bLastReportedPie = false;
	bool bCommandRunning = false;
	bool bHadFirstRead = false;
	bool bStopAgentPieOnTake = true;
	FString LastError;
};
