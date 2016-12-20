using NBitcoin;
using Newtonsoft.Json.Linq;
using QBitNinja.Client;
using QBitNinja.Client.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using static System.Console;

namespace BlockchainProofProofOfConcept
{
	class Program
	{
		static void Main(string[] args)
		{
			//var key = new Key();
			//WriteLine(key.GetBitcoinSecret(Network.TestNet)); //cNCFWet2AdJv7zK6ThwLWJhQSR8MiVYvyr5SpV166c9f6S5pLD1P			
			//WriteLine(key.GetBitcoinSecret(Network.TestNet).GetAddress()); //mzaU8irz8cjxHz6vUUL2Fyfm5rZ5zKwDY9

			var secret = new BitcoinSecret("cNCFWet2AdJv7zK6ThwLWJhQSR8MiVYvyr5SpV166c9f6S5pLD1P");
			var bytes = Encoding.UTF8.GetBytes("foo");
			for (int i = 0; i < 100; i++)
			{
				var txid = MakePayment(secret.ToWif(), bytes);
				WriteLine($"TxId: {txid}");
			}

			WriteLine("Press key to exit...");
			ReadKey();
		}

		/// <summary>
		/// Puts data to the blockchain
		/// </summary>
		/// <param name="privateKey">you need to have money on it</param>
		/// <param name="data"></param>
		/// <returns>transaction id</returns>
		static string MakePayment(string privateKey, byte[] data)
		{
			BitcoinSecret secret = new BitcoinSecret(privateKey);
			var changeScriptPubKey = secret.ScriptPubKey;

			#region TxFee
			// Get and calculate fee					
			WriteLine("Calculating dynamic transaction fee...");
			Money feePerBytes = null;
			try
			{
				feePerBytes = QueryFeePerBytes();
			}
			catch
			{
				feePerBytes = new Money(0.001m, MoneyUnit.BTC);
			}

			int inNum = 1; // send from 1 address
			int outNum = 1; // send back to the same 1 address
			int maxOpRetOutSize = 80; // max size of OP_RETURN output is 80 byte

			// Here is how you estimate the fee: http://bitcoin.stackexchange.com/questions/1195/how-to-calculate-transaction-size-before-sending
			int estimatedTxSize = inNum * 148 + outNum * 34 + 10 + inNum + maxOpRetOutSize;
			WriteLine($"Estimated tx size: {estimatedTxSize} bytes");
			Money fee = feePerBytes * estimatedTxSize;
			WriteLine($"Fee: {fee.ToDecimal(MoneyUnit.BTC).ToString("0.#############################")}btc");
			#endregion

			//sidenote: I guess you need to deal with the dust limit, too, the testnet doesnt care
			//something like checking how much money have been sent to the changeScriptPubKey
			//somehow like this:
			var dustLimit = new Money(0.001m, MoneyUnit.BTC);
			var minimumNeededAmount = fee + dustLimit;

			#region FindCoinsToSpen
			// Gather coins can be spend
			WriteLine("Gathering unspent coins...");
			Dictionary<Coin, bool> unspentCoins = GetUnspentCoins(secret);

			// Select coins
			WriteLine("Selecting coins...");
			var coinsToSpend = new HashSet<Coin>();
			var unspentConfirmedCoins = new List<Coin>();
			var unspentUnconfirmedCoins = new List<Coin>();
			foreach (var elem in unspentCoins)
				if (elem.Value) unspentConfirmedCoins.Add(elem.Key);
				else unspentUnconfirmedCoins.Add(elem.Key);

			bool haveEnough = SelectCoins(ref coinsToSpend, minimumNeededAmount, unspentConfirmedCoins);
			if (!haveEnough)
				haveEnough = SelectCoins(ref coinsToSpend, minimumNeededAmount, unspentUnconfirmedCoins);
			if (!haveEnough)
				throw new Exception("Not enough funds.");
			#endregion

			#region AddData
			// add data to the transaction
			var scriptPubKey = TxNullDataTemplate.Instance.GenerateScriptPubKey(data);
			#endregion			

			// Build the transaction
			WriteLine("Signing transaction...");
			var builder = new TransactionBuilder();
			var tx = builder
				.AddCoins(coinsToSpend)
				.AddKeys(secret)
				.Send(scriptPubKey, Money.Zero)
				.SetChange(changeScriptPubKey)	
				.SendFees(fee)
				.BuildTransaction(true);

			#region Broadcast
			if (!builder.Verify(tx))
				throw new Exception("Couldn't build the transaction.");

			WriteLine($"Transaction Id: {tx.GetHash()}");

			var qBitClient = new QBitNinjaClient(secret.Network);

			// QBit's success response is buggy so let's check manually, too		
			BroadcastResponse broadcastResponse;
			var success = false;
			var tried = 0;
			var maxTry = 7;
			do
			{
				tried++;
				WriteLine($"Try broadcasting transaction... ({tried})");
				broadcastResponse = qBitClient.Broadcast(tx).Result;
				var getTxResp = qBitClient.GetTransaction(tx.GetHash()).Result;
				if (getTxResp == null)
				{
					Thread.Sleep(3000);
					continue;
				}
				else
				{
					success = true;
					break;
				}
			} while (tried <= maxTry);
			if (!success)
			{
				if (broadcastResponse.Error != null)
				{
					WriteLine($"Error code: {broadcastResponse.Error.ErrorCode} Reason: {broadcastResponse.Error.Reason}");
				}
				throw new Exception($"The transaction might not have been successfully broadcasted. Please check the Transaction ID in a block explorer.");
			}

			WriteLine("Transaction is successfully propagated on the network.", ConsoleColor.Green);
			#endregion

			return tx.GetHash().ToString(); // txid
		}

		private static Money QueryFeePerBytes()
		{
			try
			{

				using (var client = new HttpClient())
				{
					const string request = @"https://api.blockcypher.com/v1/btc/main";
					var result = client.GetAsync(request, HttpCompletionOption.ResponseContentRead).Result;

					var json = JObject.Parse(result.Content.ReadAsStringAsync().Result);
					var slowest = (int)(json.Value<decimal>("low_fee_per_kb") / 1024);
					return new Money(slowest, MoneyUnit.Satoshi);
				}			
			}
			catch
			{
				using (var client = new HttpClient())
				{
					const string request = @"https://bitcoinfees.21.co/api/v1/fees/recommended";
					var result = client.GetAsync(request, HttpCompletionOption.ResponseContentRead).Result;

					var json = JObject.Parse(result.Content.ReadAsStringAsync().Result);
					var slowest = json.Value<decimal>("hourFee");
					return new Money(slowest, MoneyUnit.Satoshi);
				}
			}
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="secrets"></param>
		/// <returns>dictionary with coins and if confirmed</returns>
		public static Dictionary<Coin, bool> GetUnspentCoins(BitcoinSecret secret)
		{
			var unspentCoins = new Dictionary<Coin, bool>();
			var destination = secret.GetAddress();

			var client = new QBitNinjaClient(secret.Network);
			var balanceModel = client.GetBalance(destination, unspentOnly: true).Result;
			foreach (var operation in balanceModel.Operations)
			{
				foreach (var elem in operation.ReceivedCoins.Select(coin => coin as Coin))
				{
					unspentCoins.Add(elem, operation.Confirmations > 0);
				}
			}			

			return unspentCoins;
		}

		public static bool SelectCoins(ref HashSet<Coin> coinsToSpend, Money totalOutAmount, List<Coin> unspentCoins)
		{
			var haveEnough = false;
			foreach (var coin in unspentCoins.OrderByDescending(x => x.Amount))
			{
				coinsToSpend.Add(coin);
				// if doesn't reach amount, continue adding next coin
				if (coinsToSpend.Sum(x => x.Amount) < totalOutAmount) continue;
				else
				{
					haveEnough = true;
					break;
				}
			}

			return haveEnough;
		}
	}
}
