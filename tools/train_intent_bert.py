#!/usr/bin/env python3
"""
train_intent_bert.py
Обучение RuBERT-tiny2 классификатора интентов для Kouki и экспорт в ONNX.

8 интентов:
  0: add_task
  1: list_tasks
  2: complete_task
  3: add_reminder
  4: list_reminders
  5: complete_reminder
  6: ask
  7: chat

Скрипт собирает обучающий датасет:
  1. Синтетические эталоны и шаблоны с вариациями (глаголы, формы, вводные слова, пунктуация).
  2. Реальные фразы из SQLite базы Kouki (/var/lib/kouki/db/kouki.sqlite).
  3. Обучает BertForSequenceClassification на базе cointegrated/rubert-tiny2 (3 эпохи).
  4. Проверяет качество (F1 > 0.95) на тестовой выборке.
  5. Экспортирует модель в ONNX с динамическими осями и сохраняет vocab.txt и intents.json.
"""

import os
import sys
import json
import sqlite3
import random
import argparse
import numpy as np

import torch
from torch.utils.data import Dataset, DataLoader
from torch.optim import AdamW
from transformers import AutoTokenizer, AutoModelForSequenceClassification
from sklearn.model_selection import train_test_split
from sklearn.metrics import classification_report, f1_score

INTENTS = [
    "add_task",
    "list_tasks",
    "complete_task",
    "add_reminder",
    "list_reminders",
    "complete_reminder",
    "ask",
    "chat"
]

INTENT2ID = {name: i for i, name in enumerate(INTENTS)}
ID2INTENT = {i: name for i, name in enumerate(INTENTS)}

# Синтетический корпус
SEED_DATA = {
    "add_task": [
        "запиши задачу купить хлеб",
        "запиши задачу купить молоко и яйца",
        "надо починить кран на кухне",
        "сохрани таск сделать презентацию",
        "добавь дело позвонить врачу",
        "запиши помыть машину на выходных",
        "нужно купить новые батарейки",
        "создай задачу убраться в комнате",
        "запиши в список дел оплатить интернет",
        "поставь задачу проверить рабочую почту",
        "надо не забыть забрать посылку",
        "запиши купить корм для кота",
        "сделай задачу заказать пиццу на вечер",
        "зафиксируй таску поправить баг в коде",
        "купи билеты на поезд",
        "надо вызвать сантехника завтра",
        "запиши пожалуйста переустановить систему",
        "сохрани задачу сдать анализы в клинике",
        "добавь таск приготовить вкусный ужин",
        "надо погладить рубашку к утру",
        "запиши задачу поменять масло в автомобиле",
        "добавь задачу вынести мусор",
        "надо купить подарок на день рождения",
        "запиши забрать документы из мфц",
        "сохрани дело полить цветы на балконе",
        "нужно дописать отчет по проекту",
        "запиши задачу оплатить коммунальные услуги",
        "поставь задачу купить кофе в зернах",
        "запиши в дела почистить ноутбук от пыли",
        "надо заказать корм для собаки",
        "запиши отправить резюме в компанию",
        "создай таск настроить роутер",
        "запиши дело постирать шторы",
        "надо распечатать договор",
        "запиши задачу купить лампочки",
        "добавь в задачи поменять фильтр для воды",
        "сохрани в список дел купить кроссовки",
        "надо записаться на стрижку к мастеру",
        "запиши проверить тормоза на машине",
        "нужно сделать резервную копию базы данных",
        "добавь задачу заказать лекарства в аптеке",
        "запиши купить новый провод для зарядки",
        "надо разобрать вещи в шкафу",
        "сохрани задачу заплатить налоги",
        "запиши в список тасков собрать чемодан"
    ],
    "list_tasks": [
        "покажи мои задачи",
        "какие у меня задачи",
        "список задач",
        "что мне нужно сделать",
        "какие дела запланированы",
        "покажи список дел",
        "что у меня по задачам",
        "выведи список тасков",
        "какие задачи висят",
        "покажи открытые задачи",
        "что делать сегодня",
        "прочитай мои дела",
        "покажи все задачи",
        "список моих дел на сегодня",
        "какие таски в работе",
        "что у нас по планам",
        "список дел",
        "покажи задачи",
        "какие задачи на сегодня",
        "озвучь список задач",
        "какие дела у меня есть",
        "покажи текущие таски",
        "что запланировано на день",
        "выведи активные дела",
        "какие дела остались",
        "список активных задач",
        "покажи невыполненные задачи",
        "что там по моим задачам",
        "прочитай список задач",
        "открой список дел"
    ],
    "complete_task": [
        "выполнил задачу купить молоко",
        "закрой задачу сделать отчет",
        "готово задача помыть посуду",
        "заверши таск пофиксить баг",
        "я сделал задачу позвонить врачу",
        "отметь выполненной задачу забрать посылку",
        "закрой таску",
        "задача сделана",
        "готово",
        "выполнил",
        "сделал",
        "завершил дело",
        "пометь задачу как решенную",
        "закрой дело купить хлеб",
        "задача выполнена успешно",
        "сделал таск настроить сервер",
        "закрой последнюю задачу",
        "отметь задачу готовой",
        "заверши задачу убраться в комнате",
        "готово оплатить интернет",
        "выполнил дело помыть машину",
        "завершить задачу",
        "отметь выполненным",
        "я все сделал",
        "таск закрыт",
        "дело сделано"
    ],
    "add_reminder": [
        "напомни мне завтра в девять утра позвонить маме",
        "поставь напоминание через 15 минут выключить духовку",
        "напомни в понедельник оплатить счета",
        "создай напоминание через час проверить стирку",
        "напомни в 18:00 пойти на тренировку",
        "напомни через полчаса снять чайник с плиты",
        "поставь напоминалку на 10 утра",
        "напомни завтра купить таблетки",
        "сделай напоминание в пятницу вечером встретить гостей",
        "напомни через 10 минут забрать пиццу",
        "напоминание на 15:30 совещание с коллегами",
        "напомни через два часа выпить стакан воды",
        "напомни вечером позвонить бабушке",
        "поставь напоминание на завтра в полдень",
        "напомни через сорок минут забрать ребенка из садика",
        "создай напоминалку выпить лекарство через 20 минут",
        "напомни в субботу утром полить цветы",
        "напомни в три часа дня включить вебинар",
        "поставь напоминание через пять минут выключить воду",
        "напомни завтра в восемь проснуться"
    ],
    "list_reminders": [
        "какие у меня напоминания",
        "покажи список напоминаний",
        "что у меня в напоминалках",
        "какие напоминания стоят",
        "покажи мои напоминания",
        "список напоминалок",
        "что мне напомнить сегодня",
        "выведи напоминания на завтра",
        "какие есть активные напоминалки",
        "покажи напоминания",
        "список напоминаний на сегодня",
        "какие напоминалки активны",
        "озвучь напоминания",
        "прочитай список напоминаний",
        "что у меня запланировано по напоминаниям",
        "какие напоминания",
        "мои напоминания",
        "список моих напоминаний",
        "что нужно напомнить",
        "какие есть напоминания",
        "озвучь мои напоминания",
        "покажи все напоминания",
        "какие стоят напоминалки",
        "напоминания на сегодня",
        "напоминания на завтра",
        "что в напоминаниях",
        "прочитай все напоминалки",
        "открой список напоминаний"
    ],
    "complete_reminder": [
        "выполнил напоминание",
        "закрой напоминание выключить плиту",
        "готово с напоминанием",
        "удали напоминание позвонить другу",
        "сделал напоминалку",
        "заверши напоминание",
        "отмени напоминание на вечер",
        "выполнено напоминание принять таблетки",
        "закрой напоминалку",
        "напоминание выполнено",
        "сними напоминание",
        "удали напоминалку",
        "готово напоминание",
        "выполнил напоминалку выпить воды",
        "закрой напоминание",
        "удали напоминание",
        "сними напоминалку",
        "отмени напоминание",
        "заверши напоминалку",
        "напоминание сделано",
        "напоминалка закрыта",
        "удали эту напоминалку",
        "сделай напоминание выполненным",
        "отметь напоминание выполненным",
        "отметь напоминалку сделанной"
    ],
    "ask": [
        "сколько минут варить яйцо",
        "какая сегодня погода на улице",
        "почему небо синее",
        "кто такой Илон Маск",
        "как приготовить пасту карбонара",
        "что такое фотосинтез",
        "сколько километров до луны",
        "в каком году родился Пушкин",
        "почему шумит холодильник",
        "где находится Париж",
        "сколько будет 25 умножить на 4",
        "как переводится слово apple",
        "какой фильм посмотреть сегодня вечером",
        "расскажи про квантовые компьютеры",
        "кто автор войны и мира",
        "сколько времени варить картошку",
        "что лучше iOS или Android",
        "почему трава зеленая",
        "как называется столица Австралии",
        "когда начался двадцать первый век",
        "сколько дней в високосном году",
        "как работает двигатель внутреннего сгорания",
        "кто открыл закон всемирного тяготения",
        "какая самая высокая гора на земле",
        "почему идет дождь",
        "как научиться играть на гитаре",
        "что приготовить на обед быстро и вкусно",
        "какое сегодня число и день недели",
        "кто президент Бразилии",
        "как устроена черная дыра"
    ],
    "chat": [
        "привет",
        "здравствуй",
        "доброе утро",
        "добрый вечер",
        "добрый день",
        "как твои дела",
        "спасибо огромное за помощь",
        "большое спасибо",
        "пожалуйста",
        "ты кто такая",
        "расскажи анекдот",
        "расскажи смешную шутку",
        "поговори со мной",
        "мне скучно",
        "пока",
        "до свидания",
        "спокойной ночи",
        "хорошего дня",
        "ты молодец",
        "отлично сработано",
        "давай поболтаем",
        "как тебя зовут",
        "приятно познакомиться",
        "чем занимаешься",
        "какой твой любимый цвет",
        "ты робот или человек",
        "рад тебя слышать",
        "ты классная",
        "все супер спасибо",
        "до скорой встречи"
    ]
}

# Вспомогательные префиксы и суффиксы для аугментации
PREFIXES = ["", "слушай, ", "коуки, ", "алиса, ", "эй, ", "пожалуйста, ", "скажи, "]
SUFFIXES = ["", ".", "!", "?", " пожалуйста", " срочно"]

def generate_augmented_dataset(db_path=None):
    texts = []
    labels = []

    # 1. Синтетические с аугментацией
    for intent, phrases in SEED_DATA.items():
        intent_id = INTENT2ID[intent]
        for phrase in phrases:
            # Оригинал
            texts.append(phrase)
            labels.append(intent_id)

            # Вариации с префиксами / суффиксами
            for prefix in PREFIXES:
                if not prefix:
                    continue
                texts.append(prefix + phrase)
                labels.append(intent_id)

            for suffix in SUFFIXES:
                if not suffix or phrase.endswith(suffix):
                    continue
                texts.append(phrase + suffix)
                labels.append(intent_id)

    # 2. Реальные фразы из SQLite базы, если база доступна
    if db_path and os.path.exists(db_path):
        try:
            con = sqlite3.connect(db_path)
            cur = con.cursor()
            cur.execute("SELECT input_text, intent, label FROM classifications")
            rows = cur.fetchall()
            real_count = 0
            for input_text, intent, label in rows:
                target_intent = label or intent
                if target_intent in INTENT2ID and input_text:
                    clean_text = input_text.strip().lower()
                    if clean_text:
                        # Дублируем реальные фразы для повышения их веса
                        for _ in range(3):
                            texts.append(clean_text)
                            labels.append(INTENT2ID[target_intent])
                        real_count += 1
            print(f"Загружено {real_count} реальных фраз из {db_path}")
            con.close()
        except Exception as e:
            print(f"Предупреждение: не удалось прочитать SQLite базу {db_path}: {e}")

    # Удаление дубликатов (сохраняя метку)
    unique_pairs = list({(t, l) for t, l in zip(texts, labels)})
    random.seed(42)
    random.shuffle(unique_pairs)
    
    unique_texts = [p[0] for p in unique_pairs]
    unique_labels = [p[1] for p in unique_pairs]

    return unique_texts, unique_labels

class IntentDataset(Dataset):
    def __init__(self, encodings, labels):
        self.encodings = encodings
        self.labels = labels

    def __getitem__(self, idx):
        item = {key: torch.tensor(val[idx]) for key, val in self.encodings.items()}
        item['labels'] = torch.tensor(self.labels[idx])
        return item

    def __len__(self):
        return len(self.labels)

def train_and_export(output_dir, db_path=None, epochs=3, batch_size=32, lr=3e-5):
    os.makedirs(output_dir, exist_ok=True)
    model_name = "cointegrated/rubert-tiny2"
    print(f"Загрузка токенизатора и базовой модели: {model_name}...")
    tokenizer = AutoTokenizer.from_pretrained(model_name)
    model = AutoModelForSequenceClassification.from_pretrained(model_name, num_labels=len(INTENTS))

    texts, labels = generate_augmented_dataset(db_path)
    print(f"Всего обучающих примеров после аугментации: {len(texts)}")

    train_texts, val_texts, train_labels, val_labels = train_test_split(
        texts, labels, test_size=0.15, random_state=42, stratify=labels
    )

    print(f"Выборки: Train={len(train_texts)}, Val={len(val_texts)}")

    train_encodings = tokenizer(train_texts, truncation=True, padding=True, max_length=64)
    val_encodings = tokenizer(val_texts, truncation=True, padding=True, max_length=64)

    train_dataset = IntentDataset(train_encodings, train_labels)
    val_dataset = IntentDataset(val_encodings, val_labels)

    device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    print(f"Обучение на устройстве: {device}")
    model.to(device)
    model.train()

    train_loader = DataLoader(train_dataset, batch_size=batch_size, shuffle=True)
    val_loader = DataLoader(val_dataset, batch_size=batch_size, shuffle=False)

    optimizer = AdamW(model.parameters(), lr=lr)

    for epoch in range(epochs):
        model.train()
        total_loss = 0.0
        for batch in train_loader:
            optimizer.zero_grad()
            input_ids = batch['input_ids'].to(device)
            attention_mask = batch['attention_mask'].to(device)
            token_type_ids = batch.get('token_type_ids')
            if token_type_ids is not None:
                token_type_ids = token_type_ids.to(device)
            labels_tensor = batch['labels'].to(device)

            outputs = model(input_ids, attention_mask=attention_mask, token_type_ids=token_type_ids, labels=labels_tensor)
            loss = outputs.loss
            loss.backward()
            optimizer.step()
            total_loss += loss.item()

        avg_loss = total_loss / len(train_loader)

        # Валидация
        model.eval()
        all_preds = []
        all_targets = []
        with torch.no_grad():
            for batch in val_loader:
                input_ids = batch['input_ids'].to(device)
                attention_mask = batch['attention_mask'].to(device)
                token_type_ids = batch.get('token_type_ids')
                if token_type_ids is not None:
                    token_type_ids = token_type_ids.to(device)
                
                outputs = model(input_ids, attention_mask=attention_mask, token_type_ids=token_type_ids)
                preds = torch.argmax(outputs.logits, dim=-1).cpu().numpy()
                all_preds.extend(preds)
                all_targets.extend(batch['labels'].numpy())

        f1 = f1_score(all_targets, all_preds, average='macro')
        print(f"Эпоха {epoch+1}/{epochs} - Loss: {avg_loss:.4f} - Val Macro F1: {f1:.4f}")

    print("\nОтчет о классификации на валидации:")
    print(classification_report(all_targets, all_preds, target_names=INTENTS, digits=4))

    assert f1 >= 0.95, f"Критерий готовности не выполнен: F1={f1:.4f} < 0.95"

    # Экспорт в ONNX
    print("\nЭкспорт модели в ONNX...")
    model.eval()
    model.to('cpu')

    dummy_text = "запиши задачу купить хлеб"
    dummy_inputs = tokenizer(dummy_text, return_tensors="pt")
    onnx_path = os.path.join(output_dir, "rubert-tiny2-intents.onnx")

    input_names = ["input_ids", "attention_mask", "token_type_ids"]
    output_names = ["logits"]
    dynamic_axes = {
        "input_ids": {0: "batch_size", 1: "sequence_length"},
        "attention_mask": {0: "batch_size", 1: "sequence_length"},
        "token_type_ids": {0: "batch_size", 1: "sequence_length"},
        "logits": {0: "batch_size"}
    }

    dummy_args = (
        dummy_inputs["input_ids"],
        dummy_inputs["attention_mask"],
        dummy_inputs["token_type_ids"]
    )

    torch.onnx.export(
        model,
        dummy_args,
        onnx_path,
        input_names=input_names,
        output_names=output_names,
        dynamic_axes=dynamic_axes,
        opset_version=14,
        do_constant_folding=True,
        dynamo=False
    )
    print(f"ONNX модель успешно сохранена: {onnx_path} ({os.path.getsize(onnx_path) / 1024 / 1024:.2f} МБ)")

    # Сохраняем vocab.txt и метаданные
    tokenizer.save_vocabulary(output_dir)
    print(f"Словарь токенизатора сохранен в: {output_dir}")

    meta = {
        "model_name": "rubert-tiny2-intents",
        "base_model": model_name,
        "classes": INTENTS,
        "intent2id": INTENT2ID,
        "id2intent": ID2INTENT,
        "threshold": 0.75,
        "f1_macro": float(f1)
    }
    with open(os.path.join(output_dir, "intents.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=2)

    print("Экспорт метаданных завершен успешно.")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Обучение RuBERT-tiny2 классификатора интентов")
    parser.add_argument("--output-dir", default="models/intent", help="Каталог сохранения ONNX и словаря")
    parser.add_argument("--db-path", default=None, help="Путь к SQLite базе Kouki")
    parser.add_argument("--epochs", type=int, default=3, help="Количество эпох")
    parser.add_argument("--batch-size", type=int, default=32, help="Размер батча")
    parser.add_argument("--lr", type=float, default=4e-5, help="Learning rate")
    args = parser.parse_args()

    train_and_export(args.output_dir, args.db_path, args.epochs, args.batch_size, args.lr)
